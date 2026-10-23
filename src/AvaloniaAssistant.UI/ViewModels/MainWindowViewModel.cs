using System.Collections.ObjectModel;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly ISessionStore _sessions;
    private readonly Dictionary<string, ChatSession> _sessionCache = new(StringComparer.Ordinal);

    public MainWindowViewModel(
        IAssistantRuntime runtime,
        ISettingsProvider settings,
        ISessionStore sessions,
        IFilePicker picker,
        IVoiceInput voice)
    {
        _sessions = sessions;
        Runtime = runtime;
        SettingsProvider = settings;
        Picker = picker;
        Voice = voice;
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

    public AssistantSettings Settings => SettingsProvider.Current;

    public HomeViewModel Home { get; }

    public ObservableCollection<SessionGroupViewModel> SessionGroups { get; } = [];

    public ObservableCollection<WorkspaceItemViewModel> Workspaces { get; } = [];

    public string UserName => string.IsNullOrWhiteSpace(Settings.UserName) ? Environment.UserName : Settings.UserName;

    public string UserInitial => UserName.Length > 0 ? UserName[..1].ToUpperInvariant() : "A";

    public string PlanText => "本地运行 · 数据留在本机";

    public string VersionText => "v1.0.0";

    public string SessionCountText => $"任务 ({SessionGroups.Sum(group => group.Items.Count)})";

    public bool HasNoSessions => SessionGroups.Count == 0;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(Settings.ApiKey);

    public bool IsHomeSelected => CurrentPage is HomeViewModel;

    public bool IsToolsSelected => CurrentPage is ToolsViewModel;

    public bool IsMcpSelected => CurrentPage is McpViewModel;

    public bool IsLibrarySelected => CurrentPage is LibraryViewModel;

    public bool IsSettingsSelected => CurrentPage is SettingsViewModel;

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
    public void OpenLibrary() => CurrentPage = new LibraryViewModel(this);

    [RelayCommand]
    public async Task OpenSettingsAsync()
    {
        CurrentPage = new SettingsViewModel(this);
        await Task.CompletedTask;
    }

    [RelayCommand]
    private void ToggleSidebar() => IsSidebarVisible = !IsSidebarVisible;

    public async Task StartNewChatAsync(string? prompt)
    {
        var chat = new ChatViewModel(this, null);
        CurrentPage = chat;
        await SetActiveSessionAsync(null);

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            await chat.SendPromptAsync(prompt);
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
        await _sessions.SaveAsync(session);
        await ReloadSessionsAsync();
        await SetActiveSessionAsync(session.Id);
    }

    public async Task DeleteSessionAsync(string sessionId)
    {
        await _sessions.DeleteAsync(sessionId);
        _sessionCache.Remove(sessionId);

        if (CurrentPage is ChatViewModel chat && chat.Session.Id == sessionId)
        {
            CurrentPage = Home;
        }

        await ReloadSessionsAsync();
    }

    /// <summary>侧边栏「添加工作区」和资料库入口共用：选一个目录，设为当前工作区并记住它。</summary>
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

    public void SelectWorkspace(string folder) => _ = SelectWorkspaceAsync(folder);

    public async Task SelectWorkspaceAsync(string folder)
    {
        var settings = Settings;
        settings.WorkspacePath = folder;
        settings.RecentWorkspaces.RemoveAll(path => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase));
        settings.RecentWorkspaces.Insert(0, folder);

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

        var activeId = (CurrentPage as ChatViewModel)?.Session.Id;
        var groups = new List<SessionGroupViewModel>();

        foreach (var group in sessions
                     .GroupBy(session => GroupName(session.UpdatedAt))
                     .OrderBy(group => GroupOrder(group.Key)))
        {
            var items = group
                .OrderByDescending(session => session.UpdatedAt)
                .Select(session => new SessionItemViewModel(
                    session,
                    id => _ = OpenSessionAsync(id),
                    id => _ = DeleteSessionAsync(id))
                {
                    IsActive = session.Id == activeId,
                });

            groups.Add(new SessionGroupViewModel(group.Key, items));
        }

        SessionGroups.Clear();
        foreach (var group in groups)
        {
            SessionGroups.Add(group);
        }

        OnPropertyChanged(nameof(SessionCountText));
        OnPropertyChanged(nameof(HasNoSessions));
    }

    public async Task ReloadWorkspacesAsync()
    {
        var settings = Settings;
        var paths = new List<string>();
        if (!string.IsNullOrWhiteSpace(settings.WorkspacePath))
        {
            paths.Add(settings.WorkspacePath);
        }

        foreach (var path in settings.RecentWorkspaces)
        {
            if (!paths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                paths.Add(path);
            }
        }

        await Ui.OnUiAsync(() =>
        {
            Workspaces.Clear();
            foreach (var path in paths.Take(6))
            {
                Workspaces.Add(new WorkspaceItemViewModel(path, SelectWorkspace)
                {
                    IsActive = string.Equals(path, settings.WorkspacePath, StringComparison.OrdinalIgnoreCase),
                });
            }
        });
    }

    public void LoadDemoChat()
    {
        var session = DemoData.CreateSession(Settings.WorkspacePath);
        CurrentPage = new ChatViewModel(this, session);
    }

    public void LoadDemoSidebar()
    {
        var demo = new List<SessionItemViewModel>
        {
            new(new ChatSession
            {
                Id = "demo",
                Title = "整理电脑配置清单",
                UpdatedAt = DateTimeOffset.Now.AddMinutes(-12),
            }, _ => { }, _ => { })
            {
                IsActive = true,
            },
            new(new ChatSession
            {
                Id = "demo-2",
                Title = "把周报改成三段式",
                UpdatedAt = DateTimeOffset.Now.AddHours(-3),
            }, _ => { }, _ => { }),
            new(new ChatSession
            {
                Id = "demo-3",
                Title = "下载目录大文件排查",
                UpdatedAt = DateTimeOffset.Now.AddDays(-1),
            }, _ => { }, _ => { }),
        };

        SessionGroups.Clear();
        SessionGroups.Add(new SessionGroupViewModel("今天", demo.Take(2)));
        SessionGroups.Add(new SessionGroupViewModel("昨天", demo.Skip(2)));
        OnPropertyChanged(nameof(SessionCountText));
        OnPropertyChanged(nameof(HasNoSessions));
    }

    private async Task SetActiveSessionAsync(string? sessionId)
    {
        await Ui.OnUiAsync(() =>
        {
            foreach (var item in SessionGroups.SelectMany(group => group.Items))
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
        OnPropertyChanged(nameof(IsLibrarySelected));
        OnPropertyChanged(nameof(IsSettingsSelected));
    }

    private static string GroupName(DateTimeOffset updatedAt)
    {
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
