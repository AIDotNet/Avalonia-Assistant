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
    private readonly Action<string> _togglePin;
    private readonly Func<string, string, Task<string?>> _rename;

    public SessionItemViewModel(
        ChatSession session,
        string workspaceName,
        Action<string> open,
        Action<string> delete,
        Func<string, string, Task<string?>> rename,
        string matchSnippet = "",
        string? displayTitle = null,
        Action<string>? togglePin = null)
    {
        Id = session.Id;
        Apply(session, displayTitle);
        MatchSnippet = matchSnippet;
        WorkspaceName = string.IsNullOrWhiteSpace(workspaceName) ? "普通对话" : workspaceName;
        ModeText = session.Mode switch
        {
            "code" or "coding" => "代码开发",
            "design" => "设计",
            _ => "办公",
        };
        _open = open;
        _delete = delete;
        _rename = rename;
        _togglePin = togglePin ?? (_ => { });
    }

    public string Id { get; }

    [ObservableProperty]
    private string title = string.Empty;

    /// <summary>置顶的常用任务。置顶只改排序，不改「更新于」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PinLabel))]
    private bool isPinned;

    /// <summary>菜单里那行字：已经置顶的显示「取消置顶」。</summary>
    public string PinLabel => IsPinned ? "取消置顶" : "置顶";

    /// <summary>列表里显示的名字。重名时会带日期或序号，存档标题不受影响。</summary>
    [ObservableProperty]
    private string displayTitle = string.Empty;

    /// <summary>搜索命中在正文里的那一句。只有搜正文命中时才有，标题命中时留空。</summary>
    public string MatchSnippet { get; }

    public bool HasMatchSnippet => !string.IsNullOrWhiteSpace(MatchSnippet);

    /// <summary>默认显示在行尾，鼠标移到这一行时让位给「⋯」。</summary>
    [ObservableProperty]
    private string timeText = string.Empty;

    public string WorkspaceName { get; }

    public string ModeText { get; }

    [ObservableProperty]
    private string updatedText = string.Empty;

    /// <summary>这一行对应的最后更新时间。同组里按它倒序排，保存后要按新时间挪位置。</summary>
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// 用最新的会话状态刷新这一行。每轮保存都调它，不再整表重建——
    /// 重建会把行对象全部换掉，挂在行上的悬停卡（450ms 定时器）会被掐断。
    /// </summary>
    public void Apply(ChatSession session, string? displayTitle)
    {
        Title = session.Title;
        DisplayTitle = string.IsNullOrWhiteSpace(displayTitle) ? session.Title : displayTitle;
        IsPinned = session.Pinned;
        UpdatedAt = session.UpdatedAt;
        TimeText = SessionText.RelativeTime(session.UpdatedAt);
        UpdatedText = "更新于 " + session.UpdatedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm");
    }

    /// <summary>重算相对时间。侧栏每分钟调一次，避免「5 分钟前」一直挂在那儿。</summary>
    public void RefreshTime() => TimeText = SessionText.RelativeTime(UpdatedAt);

    [ObservableProperty]
    private bool isActive;

    /// <summary>悬停卡片是否展开。由视图在鼠标进入/离开这一行时置位。</summary>
    [ObservableProperty]
    private bool isCardOpen;

    /// <summary>这一轮还在跑。切走之后也要看得见，否则不知道任务到底跑没跑完。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRunningSpinner))]
    private bool isRunning;

    /// <summary>
    /// 这一轮停在人工确认上。和「正在跑」分开：一个在消耗 token，一个在等用户，
    /// 侧栏用同一个转圈表示这两件事，用户会把等待读成计算。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowRunningSpinner))]
    private bool needsApproval;

    /// <summary>等待确认时不再转圈，换成盾牌；两个图标叠在同一位置会糊成一团。</summary>
    public bool ShowRunningSpinner => IsRunning && !NeedsApproval;

    /// <summary>后台跑完了，用户还没回去看。打开这条任务就清掉。</summary>
    [ObservableProperty]
    private bool hasUnread;

    [RelayCommand]
    private void Open() => _open(Id);

    /// <summary>「⋯」菜单里的删除，用户已经在菜单里点过一次，直接删。</summary>
    public void Remove() => _delete(Id);

    /// <summary>置顶 / 取消置顶。</summary>
    public void TogglePin() => _togglePin(Id);

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
        Name = WorkspaceNaming.ShortName(path) is { Length: > 0 } name ? name : "未设置";
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

    /// <summary>点标题：只展开/收起空间下的对话，不改变当前工作区（那是个显式动作，在 ⋯ 菜单里）。</summary>
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
