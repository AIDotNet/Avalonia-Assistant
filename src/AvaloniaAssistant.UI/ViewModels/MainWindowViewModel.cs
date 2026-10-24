using System.Collections.ObjectModel;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private const int MaxWorkspaces = 6;

    private readonly ISessionStore _sessions;
    private readonly Dictionary<string, ChatSession> _sessionCache = new(StringComparer.Ordinal);

    /// <summary>没发出去的草稿。按会话 id 存，页面被换走再回来还能接上。</summary>
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

    /// <summary>搜索之后还剩几条任务，给「任务 (n)」用。</summary>
    private int _visibleSessionCount;

    /// <summary>「任务」区是展开还是收起。null 表示用户还没动过，按有没有空间来定。</summary>
    private bool? _taskSectionExpanded;

    public MainWindowViewModel(
        IAssistantRuntime runtime,
        ISettingsProvider settings,
        ISessionStore sessions,
        IFilePicker picker,
        IVoiceInput voice,
        WorkspaceFileSearch fileSearch)
    {
        _sessions = sessions;
        Runtime = runtime;
        SettingsProvider = settings;
        Picker = picker;
        Voice = voice;
        FileSearch = fileSearch;
        Home = new HomeViewModel(this);

        // SaveAsync 是在线程池上抛事件的，界面更新必须回 UI 线程，否则属性变更会跨线程打到绑定上。
        SettingsProvider.Changed += (_, _) => Ui.Post(() =>
        {
            Home.Refresh();
            OnPropertyChanged(nameof(IsConfigured));
            OnPropertyChanged(nameof(UserName));
            OnPropertyChanged(nameof(UserInitial));
            _ = ReloadWorkspacesAsync();
        });
    }

    public IAssistantRuntime Runtime { get; }

    public ISettingsProvider SettingsProvider { get; }

    public IFilePicker Picker { get; }

    public IVoiceInput Voice { get; }

    /// <summary>@ 时的工作区文件索引。带缓存，首页和对话页的输入框共用一个实例。</summary>
    public WorkspaceFileSearch FileSearch { get; }

    public AssistantSettings Settings => SettingsProvider.Current;

    public HomeViewModel Home { get; }

    public ObservableCollection<SessionGroupViewModel> SessionGroups { get; } = [];

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    public string UserName => string.IsNullOrWhiteSpace(Settings.UserName) ? Environment.UserName : Settings.UserName;

    public string UserInitial => UserName.Length > 0 ? UserName[..1].ToUpperInvariant() : "A";

    public string PlanText => "本地运行 · 数据留在本机";

    public string VersionText => "v1.0.0";

    /// <summary>「任务」是全部任务的总入口：归了空间也还是任务，不从这里消失。</summary>
    public string SessionCountText => IsSearching
        ? $"任务 ({_visibleSessionCount})"
        : $"任务 ({_sessionCache.Count})";

    /// <summary>「空间」标题右侧跟着条数，和「任务」保持一致；一个都没有时不挂个 0。</summary>
    public string WorkspaceCountText => HasWorkspaces ? $"空间 ({Workspaces.Count})" : "空间";

    public bool HasNoSessions => _sessionCache.Count == 0;

    /// <summary>一条任务都没有（且没在搜索）时才提示「新建任务」。</summary>
    public bool ShowEmptyTasksHint => _sessionCache.Count == 0 && !IsSearching;

    /// <summary>搜索词没命中任何任务。</summary>
    public bool ShowNoSearchResults => IsSearching && !HasSearchResults;

    /// <summary>
    /// 还没有空间时「任务」默认展开——第一眼就该看见刚建的任务；
    /// 任务都挂在空间下之后默认收起，不让同一批任务在侧栏里占两遍位置。
    /// </summary>
    public bool IsTaskSectionExpanded => _taskSectionExpanded ?? Workspaces.Count == 0;

    /// <summary>「空间」里有没有东西可点；没有就说明当前是普通任务。</summary>
    public bool HasWorkspaces => Workspaces.Count > 0;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Settings.ApiKey);

    public bool IsHomeSelected => CurrentPage is HomeViewModel;

    public bool IsToolsSelected => CurrentPage is ToolsViewModel;

    public bool IsMcpSelected => CurrentPage is McpViewModel;

    public bool IsSettingsSelected => CurrentPage is SettingsViewModel;

    /// <summary>Ctrl+K 让侧栏的搜索框拿焦点。宿主窗口发信号，侧栏自己接。</summary>
    public event EventHandler? SearchRequested;

    public void RequestSearch() => SearchRequested?.Invoke(this, EventArgs.Empty);

    public string? TryGetDraft(string sessionId)
        => _drafts.TryGetValue(sessionId, out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    public void SaveDraft(string sessionId, string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            _drafts.Remove(sessionId);
        }
        else
        {
            _drafts[sessionId] = text;
        }
    }

    /// <summary>
    /// 还没发过一句话的新任务没有稳定 id 可用，草稿单独放一格：
    /// 从这里跳去设置、回来再「新建任务」，刚写的字还能接上。
    /// </summary>
    private string? _pendingDraft;

    public void SavePendingDraft(string? text)
        => _pendingDraft = string.IsNullOrWhiteSpace(text) ? null : text;

    public string? TakePendingDraft()
    {
        var draft = _pendingDraft;
        _pendingDraft = null;
        return draft;
    }

    /// <summary>任务搜索词。列表按它过滤，「任务」计数跟着变。</summary>
    [ObservableProperty]
    private string sessionQuery = string.Empty;

    partial void OnSessionQueryChanged(string value) => RebuildSidebar();

    public bool IsSearching => !string.IsNullOrWhiteSpace(SessionQuery);

    /// <summary>搜索之后还剩几条。没搜的时候恒为 true。</summary>
    [ObservableProperty]
    private bool hasSearchResults = true;

    public string SearchEmptyText => $"没有匹配「{SessionQuery.Trim()}」的任务";

    [ObservableProperty]
    private object? currentPage;

    [ObservableProperty]
    private bool isSidebarVisible = true;

    public async Task InitializeAsync()
    {
        await SettingsProvider.LoadAsync();
        await ReloadSessionsAsync();
        await ReloadWorkspacesAsync();
        Home.Refresh();
        CurrentPage = Home;
    }

    [RelayCommand]
    private async Task NewTaskAsync()
    {
        var chat = new ChatViewModel(this, null);
        CurrentPage = chat;
        await SetActiveSessionAsync(null);
    }

    [RelayCommand]
    public void OpenHome() => CurrentPage = Home;

    [RelayCommand]
    public void OpenTools() => CurrentPage = new ToolsViewModel(this);

    [RelayCommand]
    public void OpenMcp() => CurrentPage = new McpViewModel(this);

    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        CurrentPage = new SettingsViewModel(this);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    /// <summary>点「任务」标题：展开/收起全部任务。</summary>
    [RelayCommand]
    private void ToggleTaskSection()
    {
        _taskSectionExpanded = !IsTaskSectionExpanded;
        OnPropertyChanged(nameof(IsTaskSectionExpanded));
    }

    public Task StartNewChatAsync(string? prompt)
        => OpenNewChatAsync(string.IsNullOrWhiteSpace(prompt) ? null : PromptRequest.Plain(prompt));

    /// <summary>带着资料引用开新任务：首页里 @ 了资料也能直接发，不用先进对话再引用一遍。</summary>
    public Task StartNewChatAsync(PromptRequest request) => OpenNewChatAsync(request);

    private async Task OpenNewChatAsync(PromptRequest? request)
    {
        var chat = new ChatViewModel(this, null);
        CurrentPage = chat;
        await SetActiveSessionAsync(null);

        if (request is not null)
        {
            await chat.SendPromptAsync(request);
        }
    }

    public async Task OpenSessionAsync(string sessionId)
    {
        if (!_sessionCache.TryGetValue(sessionId, out var session))
        {
            var loaded = await _sessions.LoadAllAsync();
            session = loaded.FirstOrDefault(item => item.Id == sessionId);
            if (session is null)
            {
                return;
            }

            _sessionCache[session.Id] = session;
        }

        CurrentPage = new ChatViewModel(this, session);
        await SetActiveSessionAsync(session.Id);
    }

    public async Task SaveSessionAsync(ChatSession session)
    {
        _sessionCache[session.Id] = session;

        // 正在聊的这一轮要看得见：空间被收起时自动展开，别让新对话存进去却找不到。
        await ExpandWorkspaceAsync(session);

        await _sessions.SaveAsync(session);
        await ReloadSessionsAsync();
        await SetActiveSessionAsync(session.Id);
    }

    /// <summary>
    /// 新任务第一句话一发出就先落盘。侧栏是按存档重建的，等整轮跑完再写文件的话，
    /// 回复还在流的时候侧栏里根本没有这张任务；中途关掉应用，连用户打的字一起丢。
    /// 这里只写文件 + 刷侧栏，不像 <see cref="SaveSessionAsync"/> 那样重新读盘——
    /// 读盘会把正在跑的会话对象换成反序列化出来的副本。
    /// </summary>
    public async Task RegisterSessionAsync(ChatSession session)
    {
        _sessionCache[session.Id] = session;

        // 空间收着就展开：任务建在看不见的地方等于没建。
        await ExpandWorkspaceAsync(session);

        await _sessions.SaveAsync(session);
        await ReloadWorkspacesAsync();
        await SetActiveSessionAsync(session.Id);
    }

    /// <summary>会话落在哪个空间，就把那个空间展开。</summary>
    private async Task ExpandWorkspaceAsync(ChatSession session)
    {
        if (string.IsNullOrWhiteSpace(session.WorkspacePath) || !IsCollapsed(Settings, session.WorkspacePath))
        {
            return;
        }

        SetCollapsed(Settings, session.WorkspacePath, false);
        await SettingsProvider.SaveAsync(Settings);
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        await _sessions.DeleteAsync(sessionId);
        _sessionCache.Remove(sessionId);
        _drafts.Remove(sessionId);

        if (CurrentPage is ChatViewModel chat && chat.Session.Id == sessionId)
        {
            CurrentPage = Home;
        }

        await ReloadSessionsAsync();
    }

    /// <summary>侧边栏「添加工作区」和输入框里的工作区切换共用：选一个目录，设为当前工作区并记住它。</summary>
    [RelayCommand]
    public async Task AddWorkspaceAsync()
    {
        var folder = await Picker.PickFolderAsync("选择工作区");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await SelectWorkspaceAsync(folder);
    }

    /// <summary>
    /// 「新建工作空间」把目录建在这里：默认工作区 Workspace 的父级，
    /// 也就是用户目录下的 AvaloniaAssistant。和默认工作区并排，找起来是同一个地方。
    /// </summary>
    public static string WorkspaceRoot { get; } =
        System.IO.Path.GetDirectoryName(AppPaths.DefaultWorkspace)
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>
    /// 建一个新目录并切过去。成功返回 null；失败返回一句能直接展示在浮层里的原因。
    /// </summary>
    public async Task<string?> CreateWorkspaceAsync(string name)
    {
        var trimmed = (name ?? string.Empty).Trim().TrimEnd('.');

        if (trimmed.Length == 0)
        {
            return "名字不能为空";
        }

        // 「.」「..」不是文件名，拼出来会变成上一级目录
        if (trimmed is "." or "..")
        {
            return "换个名字吧，这个不能当目录名";
        }

        if (trimmed.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            return "名字里不能有 \\ / : * ? \" < > |";
        }

        var target = System.IO.Path.Combine(WorkspaceRoot, trimmed);

        if (Directory.Exists(target))
        {
            return "这里已经有同名文件夹，换个名字，或者直接打开它";
        }

        if (File.Exists(target))
        {
            return "这里已经有同名文件";
        }

        try
        {
            Directory.CreateDirectory(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            return "创建失败：" + ex.Message;
        }

        // 新建的排在最前面：列表有 6 条上限，排到队尾等于新建完看不见。
        var settings = Settings;
        settings.RecentWorkspaces.RemoveAll(path => SamePath(path, target));
        settings.RecentWorkspaces.Insert(0, target);

        await SelectWorkspaceAsync(target);
        return null;
    }

    /// <summary>
    /// 浮层里的「不使用工作空间」：只把当前工作区清空，之后新建的任务按普通对话处理。
    /// 列表里的记录一条都不删，下次还能点回来。
    /// </summary>
    public async Task ClearWorkspaceAsync()
    {
        if (string.IsNullOrWhiteSpace(Settings.WorkspacePath))
        {
            return;
        }

        Settings.WorkspacePath = string.Empty;

        Home.Refresh();
        await ReloadWorkspacesAsync();
        await SettingsProvider.SaveAsync(Settings);
    }

    public void SelectWorkspace(string folder) => _ = SelectWorkspaceAsync(folder);

    public async Task SelectWorkspaceAsync(string folder)
    {
        var settings = Settings;
        settings.WorkspacePath = folder;

        // 只是切换当前工作区，不动列表顺序：空间按加入的先后排，点一下就跳位会让人找不到刚才那个。
        if (!settings.RecentWorkspaces.Contains(folder, StringComparer.OrdinalIgnoreCase))
        {
            settings.RecentWorkspaces.Add(folder);
        }

        Home.Refresh();
        await ReloadWorkspacesAsync();
        await SettingsProvider.SaveAsync(settings);
    }

    /// <summary>点空间标题：展开/收起它下面的对话，同时把这个空间设为当前工作区。</summary>
    public void ToggleWorkspace(WorkspaceItemViewModel item)
    {
        var expand = !item.IsExpanded;
        SetCollapsed(Settings, item.Path, !expand);
        item.IsExpanded = expand;

        _ = SelectWorkspaceAsync(item.Path);
    }

    /// <summary>
    /// 空间行上的「+」：在这个空间里直接开一张新对话，不再让用户挑目录。
    /// 空间如果收着就展开，否则新对话建在看不见的地方。
    /// </summary>
    public async Task NewSessionInWorkspaceAsync(string path)
    {
        var settings = Settings;
        settings.WorkspacePath = path;

        // 和切换空间一样，只改当前工作区，不把这条记录挪到列表最前面
        if (!settings.RecentWorkspaces.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            settings.RecentWorkspaces.Add(path);
        }

        SetCollapsed(settings, path, false);

        Home.Refresh();
        await ReloadWorkspacesAsync();
        await SettingsProvider.SaveAsync(settings);

        await OpenNewChatAsync(null);
    }

    /// <summary>把已有记录换成另一个目录；换的正好是当前工作区时，当前也跟着切过去。</summary>
    public async Task RepickWorkspaceAsync(string oldPath)
    {
        var folder = await Picker.PickFolderAsync("重新选择工作区目录");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        var settings = Settings;
        var wasActive = string.Equals(settings.WorkspacePath, oldPath, StringComparison.OrdinalIgnoreCase);

        // 换目录是原地替换，别把它挪到列表第一位
        var index = settings.RecentWorkspaces.FindIndex(path => SamePath(path, oldPath));
        settings.RecentWorkspaces.RemoveAll(path => SamePath(path, folder) || SamePath(path, oldPath));

        if (index < 0 || index > settings.RecentWorkspaces.Count)
        {
            settings.RecentWorkspaces.Add(folder);
        }
        else
        {
            settings.RecentWorkspaces.Insert(index, folder);
        }

        if (wasActive)
        {
            settings.WorkspacePath = folder;
        }

        // 收起状态跟着旧目录一起搬过去
        if (IsCollapsed(settings, oldPath))
        {
            SetCollapsed(settings, folder, true);
        }

        Home.Refresh();
        await ReloadWorkspacesAsync();
        await SettingsProvider.SaveAsync(settings);
    }

    /// <summary>在资源管理器里打开工作区目录。</summary>
    public void OpenWorkspaceFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!Directory.Exists(path))
        {
            return;
        }

        Ui.OpenExternal(path);
    }

    /// <summary>
    /// 改磁盘上的目录名。成功返回 null；失败返回一句能直接展示的原因。
    /// </summary>
    public async Task<string?> RenameWorkspaceAsync(string path, string newName)
    {
        var name = (newName ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return "名字不能为空";
        }

        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0)
        {
            return "名字里不能有 \\ / : * ? \" < > |";
        }

        var parent = System.IO.Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
        {
            return "这个路径不支持重命名";
        }

        var target = System.IO.Path.Combine(parent, name);
        if (string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!Directory.Exists(path))
        {
            return "目录不存在，可能已经被移动或删除";
        }

        if (Directory.Exists(target) || File.Exists(target))
        {
            return "同目录下已经有同名文件或文件夹";
        }

        try
        {
            Directory.Move(path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "重命名失败：" + ex.Message;
        }

        var settings = Settings;
        if (string.Equals(settings.WorkspacePath, path, StringComparison.OrdinalIgnoreCase))
        {
            settings.WorkspacePath = target;
        }

        var index = settings.RecentWorkspaces.FindIndex(item => SamePath(item, path));
        if (index >= 0)
        {
            settings.RecentWorkspaces[index] = target;
        }
        else
        {
            settings.RecentWorkspaces.Add(target);
        }

        // 收起状态跟着目录一起搬过去
        if (IsCollapsed(settings, path))
        {
            SetCollapsed(settings, target, true);
        }

        Home.Refresh();
        await ReloadWorkspacesAsync();
        await SettingsProvider.SaveAsync(settings);
        return null;
    }

    /// <summary>
    /// 从「空间」里去掉一条记录。去掉的如果是当前工作区，就把工作区清空——
    /// 之后新建的任务不再带工作区，按普通对话处理。
    /// </summary>
    public async Task RemoveWorkspaceAsync(string path)
    {
        var settings = Settings;
        if (string.Equals(settings.WorkspacePath, path, StringComparison.OrdinalIgnoreCase))
        {
            settings.WorkspacePath = string.Empty;
        }

        settings.RecentWorkspaces.RemoveAll(item => SamePath(item, path));
        SetCollapsed(settings, path, false);

        Home.Refresh();
        await ReloadWorkspacesAsync();
        await SettingsProvider.SaveAsync(settings);
    }

    public async Task ReloadSessionsAsync()
    {
        var sessions = await _sessions.LoadAllAsync();
        _sessionCache.Clear();
        foreach (var session in sessions)
        {
            _sessionCache[session.Id] = session;
        }

        await Ui.OnUiAsync(RebuildSidebar);
    }

    public Task ReloadWorkspacesAsync() => Ui.OnUiAsync(RebuildSidebar);

    /// <summary>
    /// 侧边栏的统一重建入口：任务按日期分组列全部，「空间」再按目录分一遍。
    /// 两张表看的是同一批会话——空间是归类，不是把任务藏起来的地方。
    /// 空间顺序只看添加顺序，点和展开都不改变它。
    /// </summary>
    private void RebuildSidebar()
    {
        var settings = Settings;
        var activeId = (CurrentPage as ChatViewModel)?.Session.Id;
        var query = SessionQuery.Trim();

        // 搜索只过滤「看得见的这一层」，不改磁盘、不改分组规则。
        var sessions = _sessionCache.Values
            .Where(session => MatchesQuery(session, query))
            .OrderByDescending(session => session.UpdatedAt)
            .ToList();

        _visibleSessionCount = sessions.Count;
        HasSearchResults = sessions.Count > 0;

        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var items = new List<WorkspaceItemViewModel>();

        foreach (var path in WorkspacePaths(settings))
        {
            var matched = sessions.Where(session => SamePath(session.WorkspacePath, path)).ToList();
            if (query.Length > 0 && matched.Count == 0 && !WorkspaceName(path).Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var item = new WorkspaceItemViewModel(
                path,
                ToggleWorkspace,
                NewSessionInWorkspaceAsync,
                OpenWorkspaceFolder,
                RenameWorkspaceAsync,
                RepickWorkspaceAsync,
                RemoveWorkspaceAsync)
            {
                IsActive = SamePath(path, settings.WorkspacePath),
                IsExpanded = !IsCollapsed(settings, path) || query.Length > 0,
            };

            foreach (var session in matched.Where(session => !claimed.Contains(session.Id)))
            {
                claimed.Add(session.Id);
                item.Sessions.Add(CreateSessionItem(session, activeId, item.Name));
            }

            item.RefreshSessions();
            items.Add(item);
        }

        Workspaces.Clear();
        foreach (var item in items)
        {
            Workspaces.Add(item);
        }

        SessionGroups.Clear();
        foreach (var group in sessions
                     .GroupBy(session => GroupName(session.UpdatedAt))
                     .OrderBy(group => GroupOrder(group.Key)))
        {
            SessionGroups.Add(new SessionGroupViewModel(
                group.Key,
                group.Select(session => CreateSessionItem(session, activeId, LooseWorkspaceName(session)))));
        }

        OnPropertyChanged(nameof(SessionCountText));
        OnPropertyChanged(nameof(HasNoSessions));
        OnPropertyChanged(nameof(ShowEmptyTasksHint));
        OnPropertyChanged(nameof(ShowNoSearchResults));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(SearchEmptyText));
        OnPropertyChanged(nameof(IsTaskSectionExpanded));
        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(WorkspaceCountText));
    }

    private SessionItemViewModel CreateSessionItem(ChatSession session, string? activeId, string workspaceName)
        => new(
            session,
            workspaceName,
            id => _ = OpenSessionAsync(id),
            id => _ = DeleteSessionAsync(id),
            RenameSessionAsync)
        {
            IsActive = session.Id == activeId,
        };

    /// <summary>改任务标题。成功返回 null，失败返回一句能直接展示的原因。</summary>
    public async Task<string?> RenameSessionAsync(string sessionId, string newTitle)
    {
        var title = (newTitle ?? string.Empty).Trim();
        if (title.Length == 0)
        {
            return "名字不能为空";
        }

        if (title.Length > 60)
        {
            return "名字太长了，控制在 60 个字以内";
        }

        if (!_sessionCache.TryGetValue(sessionId, out var session))
        {
            return "任务不存在，可能已经被删除";
        }

        // 改名不动 UpdatedAt：侧栏按最近使用排序，改个名字不该把任务顶到最前面
        session.Title = title;
        await _sessions.SaveAsync(session);
        await ReloadSessionsAsync();
        return null;
    }

    /// <summary>空间列表：按用户添加的先后排，当前工作区不在里面时补到最后。侧栏和输入框浮层共用这一份。</summary>
    internal static List<string> WorkspacePaths(AssistantSettings settings)
    {
        var paths = new List<string>();
        foreach (var path in settings.RecentWorkspaces)
        {
            if (!string.IsNullOrWhiteSpace(path) && !paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }

        if (!string.IsNullOrWhiteSpace(settings.WorkspacePath)
            && !paths.Contains(settings.WorkspacePath, StringComparer.OrdinalIgnoreCase))
        {
            paths.Add(settings.WorkspacePath);
        }

        return paths.Take(MaxWorkspaces).ToList();
    }

    internal static bool SamePath(string? left, string? right)
        => !string.IsNullOrWhiteSpace(left)
           && !string.IsNullOrWhiteSpace(right)
           && string.Equals(
               left.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
               right.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar),
               StringComparison.OrdinalIgnoreCase);

    /// <summary>这个空间是不是被收起了。收起状态跟着设置走，重启之后还是原样。</summary>
    private static bool IsCollapsed(AssistantSettings settings, string path)
        => settings.CollapsedWorkspaces?.Any(item => SamePath(item, path)) == true;

    private static void SetCollapsed(AssistantSettings settings, string path, bool collapsed)
    {
        settings.CollapsedWorkspaces ??= [];
        settings.CollapsedWorkspaces.RemoveAll(item => SamePath(item, path));

        if (collapsed)
        {
            settings.CollapsedWorkspaces.Add(path);
        }
    }

    public void LoadDemoChat()
    {
        var session = DemoData.CreateSession(Settings.WorkspacePath);
        CurrentPage = new ChatViewModel(this, session);
    }

    /// <summary>仅用于 --screenshot：塞几轮假对话，让侧栏的空间/任务分层能看出效果。</summary>
    public void LoadDemoSidebar()
    {
        var paths = WorkspacePaths(Settings);
        var now = DateTimeOffset.Now;

        _sessionCache.Clear();

        var demos = new (string Id, string Title, string? Workspace, TimeSpan Ago)[]
        {
            ("demo", "整理电脑配置清单", paths.FirstOrDefault(), TimeSpan.FromMinutes(-12)),
            ("demo-2", "把周报改成三段式", paths.FirstOrDefault(), TimeSpan.FromHours(-3)),
            ("demo-3", "下载目录大文件排查", paths.FirstOrDefault(), TimeSpan.FromDays(-1)),
            ("demo-4", "检查 pi 项目新版本并移植", paths.Skip(1).FirstOrDefault(), TimeSpan.FromHours(-6)),
            ("demo-5", "配置 GitHub 工作流并发布", paths.Skip(1).FirstOrDefault(), TimeSpan.FromMinutes(-8)),
            ("demo-6", "功能未真实生效的问题", null, TimeSpan.FromMinutes(-44)),
        };

        foreach (var demo in demos)
        {
            _sessionCache[demo.Id] = new ChatSession
            {
                Id = demo.Id,
                Title = demo.Title,
                WorkspacePath = demo.Workspace,
                CreatedAt = now + demo.Ago,
                UpdatedAt = now + demo.Ago,
            };
        }

        RebuildSidebar();

        // TEMP: 截图验证悬停卡片用
        if (Workspaces.FirstOrDefault()?.Sessions.FirstOrDefault() is { } first)
        {
            first.IsActive = true;
        }
    }

    private async Task SetActiveSessionAsync(string? sessionId)
    {
        await Ui.OnUiAsync(() =>
        {
            var items = SessionGroups
                .SelectMany(group => group.Items)
                .Concat(Workspaces.SelectMany(workspace => workspace.Sessions));

            foreach (var item in items)
            {
                item.IsActive = item.Id == sessionId;
            }
        });

        OnPropertyChanged(nameof(IsHomeSelected));
    }

    partial void OnCurrentPageChanging(object? oldValue, object? newValue)
    {
        if (oldValue is ChatViewModel chat)
        {
            chat.Dispose();
        }
    }

    partial void OnCurrentPageChanged(object? value)
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsToolsSelected));
        OnPropertyChanged(nameof(IsMcpSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }

    /// <summary>没挂到空间上的任务：以前带过工作区的话，悬停卡片里还看得到那个目录名。</summary>
    private static string LooseWorkspaceName(ChatSession session)
    {
        if (string.IsNullOrWhiteSpace(session.WorkspacePath))
        {
            return string.Empty;
        }

        return System.IO.Path.GetFileName(session.WorkspacePath.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));
    }

    private static string WorkspaceName(string path)
        => System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>搜索词命中标题或所属空间名。空词视为全通过。</summary>
    private static bool MatchesQuery(ChatSession session, string query)
    {
        if (query.Length == 0)
        {
            return true;
        }

        return session.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || LooseWorkspaceName(session).Contains(query, StringComparison.OrdinalIgnoreCase);
    }

    private static string GroupName(DateTimeOffset updatedAt)    {
        var today = DateTimeOffset.Now.Date;
        if (updatedAt.Date == today)
        {
            return "今天";
        }

        if (updatedAt.Date == today.AddDays(-1))
        {
            return "昨天";
        }

        return updatedAt.Date > today.AddDays(-7) ? "最近 7 天" : "更早";
    }

    private static int GroupOrder(string name) => name switch
    {
        "今天" => 0,
        "昨天" => 1,
        "最近 7 天" => 2,
        _ => 3,
    };
}
