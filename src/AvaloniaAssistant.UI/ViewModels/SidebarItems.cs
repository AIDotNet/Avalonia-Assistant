using System.Collections.ObjectModel;
using AvaloniaAssistant.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed class SessionGroupViewModel
{
    public SessionGroupViewModel(string header, IEnumerable<SessionItemViewModel> items)
    {
        Header = header;
        Items = new ObservableCollection<SessionItemViewModel>(items);
    }

    public string Header { get; }

    public ObservableCollection<SessionItemViewModel> Items { get; }
}

public sealed partial class SessionItemViewModel : ObservableObject
{
    private readonly Action<string> _open;
    private readonly Action<string> _delete;
    private readonly Func<string, string, Task<string?>> _rename;

    public SessionItemViewModel(
        ChatSession session,
        string workspaceName,
        Action<string> open,
        Action<string> delete,
        Func<string, string, Task<string?>> rename)
    {
        Id = session.Id;
        Title = session.Title;
        TimeText = SessionText.RelativeTime(session.UpdatedAt);
        WorkspaceName = string.IsNullOrWhiteSpace(workspaceName) ? "普通对话" : workspaceName;
        ModeText = session.Mode switch
        {
            "code" or "coding" => "代码开发",
            "design" => "设计",
            _ => "办公",
        };
        UpdatedText = "更新于 " + session.UpdatedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm");
        _open = open;
        _delete = delete;
        _rename = rename;
    }

    public string Id { get; }

    public string Title { get; }

    /// <summary>默认显示在行尾，鼠标移到这一行时让位给「⋯」。</summary>
    public string TimeText { get; }

    public string WorkspaceName { get; }

    public string ModeText { get; }

    public string UpdatedText { get; }

    [ObservableProperty]
    private bool isActive;

    /// <summary>悬停卡片是否展开。由视图在鼠标进入/离开这一行时置位。</summary>
    [ObservableProperty]
    private bool isCardOpen;

    [RelayCommand]
    private void Open() => _open(Id);

    /// <summary>「⋯」菜单里的删除，用户已经在菜单里点过一次，直接删。</summary>
    public void Remove() => _delete(Id);

    /// <summary>改名。成功返回 null，失败返回一句能直接展示的原因。</summary>
    public Task<string?> RenameAsync(string newTitle) => _rename(Id, newTitle);
}

public sealed partial class WorkspaceItemViewModel : ObservableObject
{
    private readonly Action<WorkspaceItemViewModel> _toggle;
    private readonly Func<string, Task> _newSession;
    private readonly Action<string> _open;
    private readonly Func<string, string, Task<string?>> _rename;
    private readonly Func<string, Task> _repick;
    private readonly Func<string, Task> _remove;

    public WorkspaceItemViewModel(
        string path,
        Action<WorkspaceItemViewModel> toggle,
        Func<string, Task> newSession,
        Action<string> open,
        Func<string, string, Task<string?>> rename,
        Func<string, Task> repick,
        Func<string, Task> remove)
    {
        Path = path;
        Name = string.IsNullOrWhiteSpace(path)
            ? "未设置"
            : System.IO.Path.GetFileName(path.TrimEnd(System.IO.Path.DirectorySeparatorChar)) is { Length: > 0 } name
                ? name
                : path;
        _toggle = toggle;
        _newSession = newSession;
        _open = open;
        _rename = rename;
        _repick = repick;
        _remove = remove;
    }

    public string Path { get; }

    public string Name { get; }

    /// <summary>这个空间下的对话，按最近更新时间排。</summary>
    public ObservableCollection<SessionItemViewModel> Sessions { get; } = [];

    public string CountText => Sessions.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>收起时才在标题右边露个条数，展开时对话本身就看得见，不用再挂一个数字。</summary>
    public bool ShowCount => !IsExpanded && Sessions.Count > 0;

    [ObservableProperty]
    private bool isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText))]
    [NotifyPropertyChangedFor(nameof(ShowCount))]
    private bool isExpanded = true;

    /// <summary>点标题：展开/收起空间下的对话，同时把它设为当前工作区。</summary>
    [RelayCommand]
    private void Toggle() => _toggle(this);

    /// <summary>行内「+」：不弹目录选择，直接在这个空间里开一张新对话。</summary>
    [RelayCommand]
    private Task NewSessionAsync() => _newSession(Path);

    /// <summary>对话列表填完之后刷新一下标题上的条数。</summary>
    public void RefreshSessions()
    {
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(ShowCount));
    }

    /// <summary>在资源管理器里打开这个目录。</summary>
    public void OpenFolder() => _open(Path);

    /// <summary>改目录名。成功返回 null，失败返回一句能直接给用户看的原因。</summary>
    public Task<string?> RenameAsync(string newName) => _rename(Path, newName);

    /// <summary>换一个目录顶替这条记录，原来选中的话跟着切过去。</summary>
    [RelayCommand]
    private Task RepickAsync() => _repick(Path);

    /// <summary>从列表里去掉；如果它是当前工作区，清空后这一轮任务按普通对话处理。</summary>
    [RelayCommand]
    private Task RemoveAsync() => _remove(Path);
}
