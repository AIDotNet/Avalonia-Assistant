using System.Collections.ObjectModel;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    /// <summary>工作区记录上限。到顶时浮层要把「新建 / 打开」置灰并说明原因，
    /// 以前是直接 Take(6) 静默丢掉第 7 个。</summary>
    public const int MaxWorkspaceCount = 6;

    private readonly ISessionStore _sessions;
    private readonly Dictionary<string, ChatSession> _sessionCache = new(StringComparer.Ordinal);

    /// <summary>没发出去的草稿。按会话 id 存，页面被换走再回来还能接上。</summary>
    private readonly Dictionary<string, string> _drafts = new(StringComparer.Ordinal);

    /// <summary>搜索之后还剩几条任务，给「任务 (n)」用。</summary>
    private int _visibleSessionCount;

    /// <summary>
    /// 还在跑、但页面已经切走的对话。留着这个 ViewModel，流不会断、工具调用照常走，
    /// 切回来还是原来那一屏，而不是一个「等它跑完再读盘」的空壳。
    /// </summary>
    private readonly Dictionary<string, ChatViewModel> _backgroundChats = new(StringComparer.Ordinal);

    /// <summary>后台跑完、用户还没回去看过的任务。侧栏给一个点，打开就清掉。</summary>
    private readonly HashSet<string> _unreadSessions = new(StringComparer.Ordinal);

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

    /// <summary>有任务在跑、但窗口不在前台时，让视图闪一下任务栏。</summary>
    public event EventHandler? AttentionRequested;

    /// <summary>还在跑的任务数（当前页 + 后台）。关窗保护和标题都看它。</summary>
    public int RunningChatCount
        => (CurrentPage is ChatViewModel active && active.IsBusy ? 1 : 0)
           + _backgroundChats.Values.Count(chat => chat.IsBusy);

    public bool HasRunningChats => RunningChatCount > 0;

    /// <summary>
    /// 有几条任务因为工具调用卡在人工确认上。当前页和后台都要算：
    /// 后台那条卡住时，侧栏只显示转圈，用户会以为它还在算。
    /// </summary>
    public int PendingApprovalCount
        => (CurrentPage is ChatViewModel active && active.HasPendingApproval ? 1 : 0)
           + _backgroundChats.Values.Count(chat => chat.HasPendingApproval);

    /// <summary>有工具在等用户点头。整轮已经被阻塞，标题上也要能看见。</summary>
    public bool HasPendingApproval => PendingApprovalCount > 0;

    /// <summary>
    /// 标题栏直接说"还在跑几个 / 有操作等你确认"，用户切到别的窗口也能从任务栏看出来。
    /// 审批优先：等待确认的任务本身也算 IsBusy（整轮没结束），
    /// 先判"正在执行"会让"等你确认"那条分支永远走不到——用户以为它在算，其实是在等他。
    /// </summary>
    public string WindowTitle
    {
        get
        {
            var waiting = PendingApprovalCount;
            var running = RunningChatCount;

            if (waiting > 0)
            {
                var executing = Math.Max(0, running - waiting);
                return executing > 0
                    ? $"Avalonia Assistant — 等你确认（{waiting}）· 执行中（{executing}）"
                    : $"Avalonia Assistant — 等你确认（{waiting}）";
            }

            return running > 0
                ? $"Avalonia Assistant — 正在执行（{running}）"
                : "Avalonia Assistant";
        }
    }

    /// <summary>
    /// 「任务」是全部任务的总入口：归了空间也还是任务，不从这里消失。
    /// 标题和计数写在一起（「任务 (10)」），和「空间」那行同一个写法。
    /// </summary>
    public string TaskSectionText => IsSearching
        ? $"任务 ({_visibleSessionCount})"
        : $"任务 ({_sessionCache.Count})";

    /// <summary>「空间」标题。一个都没有时不挂个 0。</summary>
    public string WorkspaceSectionText => HasWorkspaces ? $"空间 ({Workspaces.Count})" : "空间";

    /// <summary>空间区能不能整体收起。和「任务」一样是浏览用的开关，只活在这次运行里。</summary>
    private bool _workspaceSectionExpanded = true;

    /// <summary>搜索时强制展开：命中藏在收起的标题里，只看见个数字等于没搜。</summary>
    public bool IsWorkspaceSectionExpanded => IsSearching || _workspaceSectionExpanded;

    /// <summary>空间区收起来时，那句「还没有空间」的提示也一起收掉。</summary>
    public bool ShowWorkspaceEmptyHint => IsWorkspaceSectionExpanded && !HasWorkspaces;

    public bool HasNoSessions => _sessionCache.Count == 0;

    /// <summary>一条任务都没有（且没在搜索）时才提示「新建任务」。</summary>
    public bool ShowEmptyTasksHint => _sessionCache.Count == 0 && !IsSearching;

    /// <summary>搜索词没命中任何任务。</summary>
    public bool ShowNoSearchResults => IsSearching && !HasSearchResults;

    /// <summary>
    /// 还没有空间时「任务」默认展开——第一眼就该看见刚建的任务；
    /// 任务都挂在空间下之后默认收起，不让同一批任务在侧栏里占两遍位置。
    /// 搜索时强制展开：否则命中结果藏在收起的标题里，只看见个数字。
    /// </summary>
    public bool IsTaskSectionExpanded => IsSearching || (_taskSectionExpanded ?? Workspaces.Count == 0);

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

    /// <summary>搜索框右边那个 ×：一次清空，别让人一个个删。</summary>
    [RelayCommand]
    private void ClearSearch() => SessionQuery = string.Empty;

    public bool IsSearching => !string.IsNullOrWhiteSpace(SessionQuery);

    /// <summary>搜索之后还剩几条。没搜的时候恒为 true。</summary>
    [ObservableProperty]
    private bool hasSearchResults = true;

    public string SearchEmptyText => $"没有匹配「{SessionQuery.Trim()}」的任务";

    [ObservableProperty]
    private object? currentPage;

    private string? ActiveChatId => (CurrentPage as ChatViewModel)?.Session.Id;

    [ObservableProperty]
    private bool isSidebarVisible = true;

    public async Task InitializeAsync()
    {
        await SettingsProvider.LoadAsync();
        await ReloadSessionsAsync();
        await ReloadWorkspacesAsync();
        Home.Refresh();

        // 侧栏开关和宽度跟着设置走：关掉再开不该弹回来。
        IsSidebarVisible = Settings.IsSidebarVisible;
        OnPropertyChanged(nameof(IsSidebarVisible));
        OnPropertyChanged(nameof(SidebarWidth));
        OnPropertyChanged(nameof(FontScale));

        // 上次那条任务还在盘上就接着打开；文件被删了、或者本来就没开过，就照常回首页。
        if (ShouldRestoreLastSession()
            && Settings.LastSessionId is { Length: > 0 } last
            && _sessionCache.ContainsKey(last))
        {
            await OpenSessionAsync(last);
            return;
        }

        CurrentPage = Home;
    }

    /// <summary>
    /// 截图和自动化跑批默认不恢复上次会话：文档配图要的是固定首页。
    /// 但显式传了 --page 才算「要某一页」，没传时允许用临时数据目录验收恢复逻辑。
    /// </summary>
    private bool ShouldRestoreLastSession()
        => string.Equals(Settings.StartupPage, "last", StringComparison.OrdinalIgnoreCase)
           && !AppLaunchOptions.HasExplicitPage
           && string.IsNullOrWhiteSpace(AppLaunchOptions.RunPrompt);

    [RelayCommand]
    private async Task NewTaskAsync()
    {
        ClearReturnChat();

        // 新建任务默认是普通任务：上一次选的工作区不跟过来。
        // 想在某个空间里干活，就在输入框下面重新挑一个，或者点空间那一行的「+」。
        await ClearWorkspaceAsync();

        var chat = CreateChat(null);
        CurrentPage = chat;
        await SetActiveSessionAsync(null);
    }

    /// <summary>
    /// 建一个对话页，并盯着它的运行状态：侧栏的小圆点靠这个，
    /// 后台跑完之后的释放也靠这个。
    /// </summary>
    private ChatViewModel CreateChat(ChatSession? session, bool consumePendingDraft = true)
    {
        var chat = new ChatViewModel(this, session, consumePendingDraft);
        chat.PropertyChanged += OnChatPropertyChanged;
        return chat;
    }

    private void OnChatPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is not ChatViewModel chat)
        {
            return;
        }

        if (e.PropertyName == nameof(ChatViewModel.IsBusy))
        {
            SetRunningFlag(chat.Session.Id, chat.IsBusy);
        }
        else if (e.PropertyName == nameof(ChatViewModel.HasPendingApproval))
        {
            // 审批卡出现/消失时标题和侧栏都要跟着变，不能等下一轮跑完才刷
            SetApprovalFlag(chat.Session.Id, chat.HasPendingApproval);
        }
    }

    private void SetRunningFlag(string sessionId, bool running)
    {
        foreach (var item in AllSessionItems())
        {
            if (item.Id != sessionId)
            {
                continue;
            }

            item.IsRunning = running;
            if (running)
            {
                item.HasUnread = false;
            }
            else
            {
                // 这一轮结束了，还挂着「等待确认」就是错的
                item.NeedsApproval = false;
            }
        }

        if (running)
        {
            _unreadSessions.Remove(sessionId);
        }
        else if (!IsActiveSession(sessionId))
        {
            // 跑完的时候人不在这一页：留个点，别让结果悄悄躺在那儿
            _unreadSessions.Add(sessionId);
            foreach (var item in AllSessionItems().Where(item => item.Id == sessionId))
            {
                item.HasUnread = true;
            }
        }

        OnPropertyChanged(nameof(RunningChatCount));
        OnPropertyChanged(nameof(HasRunningChats));
        OnPropertyChanged(nameof(PendingApprovalCount));
        OnPropertyChanged(nameof(HasPendingApproval));
        OnPropertyChanged(nameof(WindowTitle));

        if (!running)
        {
            AttentionRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// 某条任务的审批卡出现/消失了。侧栏那一行换成琥珀色的盾牌，
    /// 别让它继续显示"正在运行"——那两件事对用户的含义完全不同。
    /// </summary>
    private void SetApprovalFlag(string sessionId, bool waiting)
    {
        foreach (var item in AllSessionItems().Where(item => item.Id == sessionId))
        {
            item.NeedsApproval = waiting;
            if (waiting)
            {
                item.HasUnread = false;
            }
        }

        OnPropertyChanged(nameof(PendingApprovalCount));
        OnPropertyChanged(nameof(HasPendingApproval));
        OnPropertyChanged(nameof(WindowTitle));

        // 人在别的页面/别的窗口：任务已经停住了，得让他知道
        if (waiting && !IsActiveSession(sessionId))
        {
            AttentionRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsActiveSession(string sessionId)
        => CurrentPage is ChatViewModel chat && chat.Session.Id == sessionId;

    /// <summary>关窗前用户选了「停止并退出」：先把已经生成的正文同步落盘，再停掉还在跑的轮次。</summary>
    public void StopAllRuns()
    {
        var running = new List<ChatViewModel>();
        if (CurrentPage is ChatViewModel active && active.IsBusy)
        {
            running.Add(active);
        }

        running.AddRange(_backgroundChats.Values.Where(item => item.IsBusy));

        foreach (var chat in running)
        {
            // 顺序不能反：停掉之后 finally 会异步落盘，而进程退出不会等它。
            // 这里先把半截回复写成同步文件，再取消请求。
            chat.FlushPartialForShutdown();
            chat.StopCommand.Execute(null);
        }
    }

    /// <summary>
    /// 关窗抢救用：同步写一次会话。SaveAsync 内部全程 ConfigureAwait(false)，
    /// 在 UI 线程上等不会死锁；写的是几百 KB 的 JSON，阻塞可以忽略。
    /// </summary>
    public void SaveSessionNow(ChatSession session)
    {
        _sessionCache[session.Id] = session;

        try
        {
            _sessions.SaveAsync(session).GetAwaiter().GetResult();
        }
        catch
        {
            // 关窗路径上保存失败不该阻止退出
        }
    }

    private IEnumerable<SessionItemViewModel> AllSessionItems()
        => SessionGroups.SelectMany(group => group.Items)
            .Concat(Workspaces.SelectMany(workspace => workspace.Sessions));

    /// <summary>
    /// 把已经跑完的后台对话放掉：存档已经写盘，重新打开会从磁盘读回来。
    /// 只在下一次切换页面时清，避免在它自己的 finally 里把 VM 拆掉。
    /// </summary>
    private void EvictFinishedBackgroundChats()
    {
        foreach (var id in _backgroundChats.Where(pair => !pair.Value.IsBusy).Select(pair => pair.Key).ToList())
        {
            var chat = _backgroundChats[id];
            _backgroundChats.Remove(id);
            chat.PropertyChanged -= OnChatPropertyChanged;
            chat.Dispose();
        }
    }

    [RelayCommand]
    public void OpenHome()
    {
        ClearReturnChat();
        CurrentPage = Home;
    }

    /// <summary>
    /// 从对话页跳去设置 / 技能 / MCP 时记住来源。以前这三个页面的「返回」一律回首页，
    /// 改完 Key 得自己在侧栏里把刚才那条任务找回来。
    /// </summary>
    private ChatViewModel? _returnChat;

    public string ReturnHint => _returnChat is null ? "返回首页" : $"返回「{_returnChat.Title}」";

    /// <summary>
    /// 当前任务真正会读写的目录：已经开口的会话按钉住的那个，否则按设置。
    /// 工作区浮层的勾选和「普通任务」判断都用它，避免浮层说一套、任务跑另一套。
    /// </summary>
    public string ActiveWorkspacePath
        => (CurrentPage as ChatViewModel)?.PinnedWorkspacePath ?? Settings.WorkspacePath;

    /// <summary>当前任务是不是已经钉住目录了（发过话的会话都算）。</summary>
    public bool IsActiveWorkspacePinned
        => (CurrentPage as ChatViewModel)?.PinnedWorkspacePath is { Length: > 0 };

    /// <summary>只记对话页：从设置跳技能再跳 MCP，返回时仍然回到最初那条任务。</summary>
    private void RememberReturnPage()
    {
        if (CurrentPage is ChatViewModel { IsBusy: false } chat)
        {
            _returnChat = chat;
            OnPropertyChanged(nameof(ReturnHint));
        }
    }

    /// <summary>去了别处（首页、新建、打开另一条任务）：先前记下的来源不再有效。</summary>
    private void ClearReturnChat()
    {
        if (_returnChat is { } chat && !ReferenceEquals(chat, CurrentPage))
        {
            chat.PropertyChanged -= OnChatPropertyChanged;
            chat.Dispose();
        }

        _returnChat = null;
        OnPropertyChanged(nameof(ReturnHint));
    }

    /// <summary>「返回」：有来源就回来源，没有才回首页。</summary>
    public void GoBack()
    {
        var target = _returnChat;
        _returnChat = null;
        OnPropertyChanged(nameof(ReturnHint));

        if (target is null)
        {
            CurrentPage = Home;
            return;
        }

        // 理论上不会同时存在；真撞上了以页面为准，别让同一个会话有两份活实例
        _backgroundChats.Remove(target.Session.Id);

        CurrentPage = target;
        _ = SetActiveSessionAsync(target.Session.Id);
    }

    [RelayCommand]
    public void OpenTools()
    {
        RememberReturnPage();
        CurrentPage = new ToolsViewModel(this);
    }

    [RelayCommand]
    public void OpenMcp()
    {
        RememberReturnPage();
        CurrentPage = new McpViewModel(this);
    }

    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        RememberReturnPage();
        CurrentPage = new SettingsViewModel(this);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task ToggleSidebarAsync()
    {
        IsSidebarVisible = !IsSidebarVisible;
        Settings.IsSidebarVisible = IsSidebarVisible;
        await SaveSettingsQuietlyAsync();
    }

    /// <summary>侧栏宽度。拖动分隔条时实时写进设置对象，松手才落盘。</summary>
    public double SidebarWidth
    {
        get => Settings.SidebarWidth;
        set
        {
            var clamped = Math.Clamp(value, 220, 380);
            if (Math.Abs(Settings.SidebarWidth - clamped) < 0.5)
            {
                return;
            }

            Settings.SidebarWidth = clamped;
            OnPropertyChanged();
        }
    }

    public Task PersistSidebarWidthAsync()
    {
        Settings.SidebarWidth = SidebarWidth;
        return SaveSettingsQuietlyAsync();
    }

    /// <summary>当前界面缩放，1 表示 100%。</summary>
    public double FontScale => Settings.FontScale;

    /// <summary>缩放变化。窗口拿它更新根布局的 LayoutTransform。</summary>
    public event EventHandler? FontScaleChanged;

    /// <summary>缩放界面。滚轮连续调整时传 persist=false，由视图合并后统一保存。</summary>
    public void SetFontScale(double scale, bool persist)
    {
        var clamped = Math.Clamp(Math.Round(scale, 2), 0.9, 1.25);
        if (Math.Abs(Settings.FontScale - clamped) < 0.001)
        {
            return;
        }

        Settings.FontScale = clamped;
        OnPropertyChanged(nameof(FontScale));
        FontScaleChanged?.Invoke(this, EventArgs.Empty);

        if (persist)
        {
            _ = SaveSettingsQuietlyAsync();
        }
    }

    /// <summary>
    /// 窗口位置、侧栏宽度这类偏好保存失败，不该弹框打断正在做的事；
    /// 设置页自己的保存仍然走它原来的错误提示。
    /// </summary>
    public async Task SaveSettingsQuietlyAsync()
    {
        try
        {
            await SettingsProvider.SaveAsync(Settings);
        }
        catch
        {
            // 偏好没落盘不影响这一轮，下次改的时候还会再写一次。
        }
    }

    /// <summary>点「任务」标题：展开/收起全部任务。</summary>
    [RelayCommand]
    private void ToggleTaskSection()
    {
        _taskSectionExpanded = !IsTaskSectionExpanded;
        OnPropertyChanged(nameof(IsTaskSectionExpanded));
    }

    /// <summary>点「空间」标题：展开/收起全部空间。空间多的时候不用一个个收。</summary>
    [RelayCommand]
    private void ToggleWorkspaceSection()
    {
        _workspaceSectionExpanded = !_workspaceSectionExpanded;
        OnPropertyChanged(nameof(IsWorkspaceSectionExpanded));
        OnPropertyChanged(nameof(ShowWorkspaceEmptyHint));
    }

    public Task StartNewChatAsync(string? prompt)
        => OpenNewChatAsync(string.IsNullOrWhiteSpace(prompt) ? null : PromptRequest.Plain(prompt));

    /// <summary>带着资料引用开新任务：首页里 @ 了资料也能直接发，不用先进对话再引用一遍。</summary>
    public Task StartNewChatAsync(PromptRequest request) => OpenNewChatAsync(request);

    private async Task OpenNewChatAsync(PromptRequest? request)
    {
        ClearReturnChat();

        var chat = CreateChat(null, consumePendingDraft: request is null);
        CurrentPage = chat;
        await SetActiveSessionAsync(null);

        if (request is not null)
        {
            await chat.SendPromptAsync(request);
        }
    }

    public async Task OpenSessionAsync(string sessionId)
    {
        ClearReturnChat();

        // 已经在看这一条，就别再建一个新 VM 把旧的挤到后台去
        if (CurrentPage is ChatViewModel current && current.Session.Id == sessionId)
        {
            await SetActiveSessionAsync(sessionId);
            return;
        }

        // 这条任务还在后台跑：直接把活的实例接回来，流不会断
        if (_backgroundChats.Remove(sessionId, out var running))
        {
            CurrentPage = running;
            await SetActiveSessionAsync(sessionId);
            return;
        }

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

        CurrentPage = CreateChat(session);
        await SetActiveSessionAsync(session.Id);
    }

    public async Task SaveSessionAsync(ChatSession session)
    {
        _sessionCache[session.Id] = session;

        // 正在聊的这一轮要看得见：空间被收起时自动展开，别让新对话存进去却找不到。
        await ExpandWorkspaceAsync(session);

        await _sessions.SaveAsync(session);
        await ReloadSessionsAsync();

        // 高亮只跟着「用户正在看的那条」走：后台任务跑完不该把侧栏选中项抢过去。
        await SetActiveSessionAsync(ActiveChatId);
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
        await SetActiveSessionAsync(ActiveChatId);
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
        // 删的正好是还在跑的那一条：先停掉，再标成已丢弃，
        // 否则它跑完还会把存档写回去，删掉的任务自己又冒出来。
        if (CurrentPage is ChatViewModel active && active.Session.Id == sessionId)
        {
            active.IsDiscarded = true;
            active.StopCommand.Execute(null);
        }
        else if (_backgroundChats.Remove(sessionId, out var background))
        {
            background.IsDiscarded = true;
            background.StopCommand.Execute(null);
            background.PropertyChanged -= OnChatPropertyChanged;
            background.Dispose();
        }

        await _sessions.DeleteAsync(sessionId);
        _sessionCache.Remove(sessionId);
        _drafts.Remove(sessionId);
        _unreadSessions.Remove(sessionId);

        // 删的正好是「返回」要去的那条：来源作废，别把人带回一个已经删掉的页面
        if (_returnChat?.Session.Id == sessionId)
        {
            _returnChat = null;
            OnPropertyChanged(nameof(ReturnHint));
        }

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
    /// <summary>
    /// 点空间标题：只展开/收起，不动「当前工作区」。
    /// 以前这里顺手调了 <see cref="SelectWorkspaceAsync"/>：用户只是想看看这组里有什么，
    /// 下一个新任务的落点却被改掉了，而且界面上没有任何提示。切换改成 ⋯ 菜单里的显式动作。
    /// </summary>
    public void ToggleWorkspace(WorkspaceItemViewModel item)
    {
        var expand = !item.IsExpanded;
        SetCollapsed(Settings, item.Path, !expand);
        item.IsExpanded = expand;
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
        // 命中正文的，额外记一句上下文，挂在那条任务下面——否则只看见标题，不知道凭什么命中。
        var snippets = new Dictionary<string, string>(StringComparer.Ordinal);
        var sessions = new List<ChatSession>(_sessionCache.Count);
        foreach (var session in _sessionCache.Values)
        {
            if (!SessionSearch.TryMatch(session, query, out var snippet))
            {
                continue;
            }

            sessions.Add(session);
            if (snippet.Length > 0)
            {
                snippets[session.Id] = snippet;
            }
        }

        sessions.Sort((left, right) => right.UpdatedAt.CompareTo(left.UpdatedAt));

        // 同名任务在列表里长得一模一样，只能靠点开确认是哪条。重名的补上日期/序号。
        var displayTitles = BuildDisplayTitles();

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
                item.Sessions.Add(CreateSessionItem(
                    session,
                    activeId,
                    item.Name,
                    snippets.GetValueOrDefault(session.Id, string.Empty),
                    displayTitles.GetValueOrDefault(session.Id, session.Title)));
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
                group.Select(session => CreateSessionItem(
                    session,
                    activeId,
                    LooseWorkspaceName(session),
                    snippets.GetValueOrDefault(session.Id, string.Empty),
                    displayTitles.GetValueOrDefault(session.Id, session.Title)))));
        }

        OnPropertyChanged(nameof(TaskSectionText));
        OnPropertyChanged(nameof(HasNoSessions));
        OnPropertyChanged(nameof(ShowEmptyTasksHint));
        OnPropertyChanged(nameof(ShowNoSearchResults));
        OnPropertyChanged(nameof(IsSearching));
        OnPropertyChanged(nameof(SearchEmptyText));
        OnPropertyChanged(nameof(IsTaskSectionExpanded));
        OnPropertyChanged(nameof(IsWorkspaceSectionExpanded));
        OnPropertyChanged(nameof(HasWorkspaces));
        OnPropertyChanged(nameof(WorkspaceSectionText));
        OnPropertyChanged(nameof(ShowWorkspaceEmptyHint));
    }

    private SessionItemViewModel CreateSessionItem(
        ChatSession session,
        string? activeId,
        string workspaceName,
        string matchSnippet,
        string displayTitle)
        => new(
            session,
            workspaceName,
            id => _ = OpenSessionAsync(id),
            id => _ = DeleteSessionAsync(id),
            RenameSessionAsync,
            matchSnippet,
            displayTitle)
        {
            IsActive = session.Id == activeId,
            IsRunning = IsChatRunning(session.Id),
            NeedsApproval = IsChatWaitingApproval(session.Id),
            HasUnread = _unreadSessions.Contains(session.Id),
        };

    /// <summary>这条任务是不是停在人工确认上：同样当前页和后台都要看。</summary>
    private bool IsChatWaitingApproval(string sessionId)
    {
        if (CurrentPage is ChatViewModel current && current.Session.Id == sessionId)
        {
            return current.HasPendingApproval;
        }

        return _backgroundChats.TryGetValue(sessionId, out var background) && background.HasPendingApproval;
    }

    /// <summary>
    /// 重名任务在侧栏补一个区分后缀：优先补日期，同一天还重名再补序号。
    /// 只影响列表显示，存档里的标题和重命名输入框仍是原文。
    /// </summary>
    private Dictionary<string, string> BuildDisplayTitles()
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var group in _sessionCache.Values
                     .GroupBy(session => session.Title.Trim(), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            var seenByDate = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var session in group.OrderByDescending(item => item.UpdatedAt))
            {
                var date = session.UpdatedAt.LocalDateTime.ToString("MM-dd");
                seenByDate[date] = seenByDate.TryGetValue(date, out var count) ? count + 1 : 1;
                var suffix = seenByDate[date] == 1 ? date : $"{date} #{seenByDate[date]}";

                // 日期放前面：放末尾会被标题的尾部省略号吃掉，两条还是长得一样。
                titles[session.Id] = $"{suffix} · {session.Title}";
            }
        }

        return titles;
    }

    /// <summary>这条任务是不是还在跑：可能正在看，也可能被放到后台了。</summary>
    private bool IsChatRunning(string sessionId)
    {
        if (CurrentPage is ChatViewModel current && current.Session.Id == sessionId)
        {
            return current.IsBusy;
        }

        return _backgroundChats.TryGetValue(sessionId, out var background) && background.IsBusy;
    }

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

        return paths.Take(MaxWorkspaceCount).ToList();
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

    public void LoadDemoChat(bool dateDemo = false)
    {
        var session = dateDemo
            ? DemoData.CreateDateDemoSession(Settings.WorkspacePath)
            : DemoData.CreateSession(Settings.WorkspacePath);
        CurrentPage = CreateChat(session);
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

            // --approval-wait 的意思是「摆出等待确认的状态」，侧栏那行也得跟着摆，
            // 否则只能截到输入框上方那条常驻提示，看不见盾牌与转圈是否打架
            if (AppLaunchOptions.ApprovalWait)
            {
                first.NeedsApproval = true;
            }
        }
    }

    private async Task SetActiveSessionAsync(string? sessionId)
    {
        if (sessionId is not null)
        {
            _unreadSessions.Remove(sessionId);

            // 记住最后看的是哪条。启动恢复只在用户选「上次会话」时才用，但记下来没有代价。
            if (!string.Equals(Settings.LastSessionId, sessionId, StringComparison.Ordinal))
            {
                Settings.LastSessionId = sessionId;
                _ = SaveSettingsQuietlyAsync();
            }
        }

        await Ui.OnUiAsync(() =>
        {
            var items = SessionGroups
                .SelectMany(group => group.Items)
                .Concat(Workspaces.SelectMany(workspace => workspace.Sessions));

            foreach (var item in items)
            {
                item.IsActive = item.Id == sessionId;
                if (item.IsActive)
                {
                    item.HasUnread = false;
                }
            }
        });

        OnPropertyChanged(nameof(IsHomeSelected));
    }

    partial void OnCurrentPageChanging(object? oldValue, object? newValue)
    {
        EvictFinishedBackgroundChats();

        // 设置页订了缩放事件，换页时退订，别让旧实例留在事件链上。
        if (oldValue is SettingsViewModel settings)
        {
            settings.Dispose();
            return;
        }

        if (oldValue is not ChatViewModel chat)
        {
            return;
        }

        // 被删掉的那条不用留：它已经被标成丢弃，跑完也不会写回存档
        if (chat.IsDiscarded)
        {
            chat.PropertyChanged -= OnChatPropertyChanged;
            chat.Dispose();
            return;
        }

        // 还在跑：页面换走，但这一轮留在后台继续走完。
        // 以前这里直接 Dispose，流会断在半截，用户也看不见它还在烧 token。
        if (chat.IsBusy)
        {
            _backgroundChats[chat.Session.Id] = chat;
            return;
        }

        // 去设置/技能/MCP 时留下的来源页：留着不拆，返回时原样接回来
        // （草稿、滚动位置、已渲染的消息都还在，不用从磁盘重读重建）
        if (ReferenceEquals(chat, _returnChat))
        {
            return;
        }

        chat.PropertyChanged -= OnChatPropertyChanged;
        chat.Dispose();
    }

    partial void OnCurrentPageChanged(object? value)
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsToolsSelected));
        OnPropertyChanged(nameof(IsMcpSelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
        OnPropertyChanged(nameof(RunningChatCount));
        OnPropertyChanged(nameof(HasRunningChats));
        OnPropertyChanged(nameof(PendingApprovalCount));
        OnPropertyChanged(nameof(HasPendingApproval));
        OnPropertyChanged(nameof(WindowTitle));
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
