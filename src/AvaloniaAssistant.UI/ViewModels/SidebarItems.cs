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

    public SessionItemViewModel(ChatSession session, Action<string> open, Action<string> delete)
    {
        Id = session.Id;
        Title = session.Title;
        TimeText = SessionText.RelativeTime(session.UpdatedAt);
        _open = open;
        _delete = delete;
    }

    public string Id { get; }

    public string Title { get; }

    public string TimeText { get; }

    [ObservableProperty]
    private bool isActive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DeleteText))]
    private bool confirmDelete;

    public string DeleteText => ConfirmDelete ? "确认" : "删除";

    [RelayCommand]
    private void Open() => _open(Id);

    [RelayCommand]
    private void Delete()
    {
        if (!ConfirmDelete)
        {
            ConfirmDelete = true;
            return;
        }

        _delete(Id);
    }
}

public sealed partial class WorkspaceItemViewModel : ObservableObject
{
    private readonly Action<WorkspaceItemViewModel> _toggle;
    private readonly Action<string> _open;
    private readonly Func<string, string, Task<string?>> _rename;
    private readonly Func<string, Task> _repick;
    private readonly Func<string, Task> _remove;

    public WorkspaceItemViewModel(
        string path,
        Action<WorkspaceItemViewModel> toggle,
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
