using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Avalonia.Media;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed partial class ChatViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _host;
    private readonly IAssistantRuntime _runtime;
    private readonly ISettingsProvider _settings;
    private CancellationTokenSource? _cts;

    /// <summary>「本会话都允许」记住的工具名。只在这次对话里有效，不写进设置。</summary>
    private readonly HashSet<string> _sessionAllowedTools = new(StringComparer.Ordinal);

    /// <summary>生成期间按 Enter 排进来的消息，按先后顺序发。以前只留一条，后到的会把先到的挤掉。</summary>
    private readonly List<PromptRequest> _queuedPrompts = [];

    /// <summary>上一轮是不是失败结束的。失败时排队的消息不再往下发，先还回输入框。</summary>
    private bool _lastRunFailed;

    /// <summary>这一轮开始时的权限。生成中切档位不改变本轮行为，只能下一轮生效。</summary>
    private PermissionMode _runStartPermission;

    /// <summary>生成中切过的档位。本轮结束后再补一条提示，不静默吞掉。</summary>
    private PermissionMode? _pendingPermission;

    /// <summary>
    /// 正在跑的这一轮的存档条目和气泡。关窗前要拿它们把已经流出来的半截回复同步写进存档——
    /// 否则「停止并退出」之后重开，那一轮只剩一个空的助手气泡。
    /// </summary>
    private ChatEntry? _activeEntry;

    private AssistantMessageViewModel? _activeAssistant;

    /// <summary>关窗时抢救过半截正文。收尾不能再把「已停止」改回「完成」。</summary>
    private bool _flushedForShutdown;

    public ChatViewModel(MainWindowViewModel host, ChatSession? session, bool consumePendingDraft = true)
    {
        _host = host;
        _runtime = host.Runtime;
        _settings = host.SettingsProvider;

        Session = session ?? new ChatSession
        {
            WorkspacePath = host.Settings.WorkspacePath,
            Mode = host.Settings.Mode,
        };

        var isNewSession = session is null;

        Composer = new ComposerViewModel(host);
        Composer.SendRequested += async (_, request) => await SendPromptAsync(request);
        Composer.QueueRequested += OnQueueRequested;
        Composer.QueueCancelled += OnQueueCancelled;
        Composer.StopRequested += (_, _) => Stop();
        Composer.PermissionChanged += OnPermissionChanged;
        Composer.ResetSessionAllowedRequested += OnResetSessionAllowed;

        Messages.CollectionChanged += OnMessagesChanged;
        _settings.Changed += OnSettingsChanged;

        // 从设置页 / 首页转一圈回来，刚才没发出去的那段话还在。
        if (isNewSession)
        {
            // 带着明确提示词开的新任务不接旧草稿：那句话属于上一个没做完的任务，
            // 顶到这里会被随后的发送立刻清掉；留在原地，等下一次「新建任务」再接。
            if (consumePendingDraft && host.TakePendingDraft() is { Length: > 0 } pending)
            {
                Composer.Text = pending;
            }
        }
        else if (host.TryGetDraft(Session.Id) is { Length: > 0 } draft)
        {
            Composer.Text = draft;
        }

        LoadEntries();
        RefreshDateHeaders();
        SyncComposerWorkspacePin();
    }

    public ChatSession Session { get; }

    /// <summary>
    /// 这条任务钉住的工作区：开口之后就不跟着设置走了。还没发过话时为空。
    /// 输入框下面那颗胶囊按它显示，页头副标题也按它显示，两处同源。
    /// </summary>
    public string? PinnedWorkspacePath => Session.Entries.Count > 0 ? Session.WorkspacePath : null;

    /// <summary>把「钉在哪个目录」同步给胶囊。开会话、切设置、跑完一轮、重跑截断之后都要对一次。</summary>
    private void SyncComposerWorkspacePin()
        => Composer.PinnedWorkspacePath = PinnedWorkspacePath;

    /// <summary>任务被删掉了：这一轮跑完不要再写回存档，否则删掉的任务会自己回来。</summary>
    public bool IsDiscarded { get; set; }

    public void Dispose()
    {
        // 页面被换走（去设置、回首页、开别的任务）时把没发出去的草稿留一份，
        // 否则几十个字跟着 ChatViewModel 一起没了。
        // 一句话都没发过的新任务还没有稳定 id，走单独的待用草稿位。
        if (Session.Entries.Count == 0)
        {
            _host.SavePendingDraft(Composer.Text);
        }
        else
        {
            _host.SaveDraft(Session.Id, Composer.Text);
        }

        _settings.Changed -= OnSettingsChanged;
        Composer.PermissionChanged -= OnPermissionChanged;
        Composer.ResetSessionAllowedRequested -= OnResetSessionAllowed;
        Composer.Dispose();
    }

    /// <summary>把「本会话都允许」的放行记录清掉，回到逐次确认。</summary>
    private void OnResetSessionAllowed(object? sender, EventArgs e)
    {
        if (_sessionAllowedTools.Count == 0)
        {
            return;
        }

        _sessionAllowedTools.Clear();
        Composer.SessionAllowedCount = 0;
        Messages.Add(new NoticeMessageViewModel("已恢复逐次确认：执行命令、调用 MCP 前会再问你。"));
        RequestScroll(true);
    }

    /// <summary>设置保存是在线程池上抛事件的，标题栏那行字得回 UI 线程再改。</summary>
    private void OnSettingsChanged(object? sender, EventArgs e)
        => Ui.Post(ApplyWorkspaceChange);

    /// <summary>
    /// 还没开口的任务跟着当前选择走：输入框下面那一下挑的工作区，
    /// 必须在发第一条之前落到这条任务上，否则界面显示 A、跑起来却是空目录。
    /// 一旦发过话就钉住——中途换目录，前面引用过的文件就对不上了。
    /// </summary>
    private void ApplyWorkspaceChange()
    {
        if (Session.Entries.Count == 0
            && !MainWindowViewModel.SamePath(Session.WorkspacePath, _settings.Current.WorkspacePath))
        {
            Session.WorkspacePath = _settings.Current.WorkspacePath;
            Composer.PinnedWorkspacePath = null;
        }

        // 已经开口的会话：胶囊仍显示钉住的那个目录，不能让设置把它带跑
        SyncComposerWorkspacePin();
        OnPropertyChanged(nameof(HeaderSubtitle));
    }

    /// <summary>
    /// 权限档位变了就在对话里留一条提示：用户得清楚接下来的命令还会不会问自己。
    /// 只进消息流，不写进会话存档，重新打开不会重复出现。
    /// </summary>
    private void OnPermissionChanged(object? sender, PermissionMode permission)
    {
        if (IsBusy)
        {
            // 这一轮用的是开始时的权限快照，切档位只对下一轮生效。
            // 先挂到 pill 上，本轮结束时再补一条提示；不能像以前那样直接 return。
            _pendingPermission = permission;
            Composer.PermissionPendingNextRun = permission != _runStartPermission;
            return;
        }

        _pendingPermission = null;
        Composer.PermissionPendingNextRun = false;
        AppendPermissionNotice(permission);
    }

    private void AppendPermissionNotice(PermissionMode permission)
    {
        Messages.Add(new NoticeMessageViewModel(PermissionNoticeText(permission)));
        RequestScroll(true);
    }

    private static string PermissionNoticeText(PermissionMode permission) => permission switch
    {
        PermissionMode.FullAccess => "已切换为完全访问：执行命令、修改文件都不再逐次确认。",
        PermissionMode.ReadOnly => "已切换为只读：不会修改文件，也不会执行命令。",
        _ => "已切换为默认权限：执行命令、调用 MCP 工具前会先问你。",
    };

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public ComposerViewModel Composer { get; }

    public string Title => Session.Title;

    public string HeaderSubtitle
    {
        get
        {
            var workspace = string.IsNullOrWhiteSpace(Session.WorkspacePath)
                ? "普通任务"
                : Path.GetFileName(Session.WorkspacePath.TrimEnd(Path.DirectorySeparatorChar));
            return $"{workspace} · {_settings.Current.Model} · {WorkModeText.Describe(Session.Mode)}";
        }
    }

    public bool HasMessages => Messages.Count > 0;

    /// <summary>刚建、还没说过话：整块消息区显示上下文和三个入口提示，而不是一片空白。</summary>
    public bool ShowEmptyState => Messages.Count == 0;

    /// <summary>用户当前是不是停在底部。由 ChatView 的滚动策略实时回写，审批和浮标都靠它判断。</summary>
    public bool IsAtBottom { get; set; } = true;

    /// <summary>停在上面时，底部飘一句「有新内容 / 有操作待确认」，点一下回到底部。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFloatingHint))]
    private string? floatingHint;

    public bool ShowFloatingHint => !string.IsNullOrEmpty(FloatingHint);

    [RelayCommand]
    private void JumpToLatest()
    {
        FloatingHint = null;
        RequestScroll(true);
        Composer.RequestFocus();
    }

    /// <summary>用户自己滚回底部了，浮标就没必要再挂着。</summary>
    public void ClearFloatingHint() => FloatingHint = null;

    /// <summary>
    /// 有工具调用在等确认。整轮 Agent 已经阻塞在这儿，
    /// 光在消息流里插一张卡不够——用户滚上去或切走就看不见了。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingApproval))]
    private string? pendingApprovalText;

    public bool HasPendingApproval => !string.IsNullOrWhiteSpace(PendingApprovalText);

    /// <summary>
    /// 上下文用量估算。数值来自上一轮 RunCompleted 的输入 + 输出 token：
    /// 下一轮要把这些重新发一遍，所以它约等于"当前上下文有多满"。
    /// 模型真实窗口不在本地，128k 是保守值（和 CodingSessionFactory 的估算保持一致）。
    /// </summary>
    private const long EstimatedContextWindow = 128_000;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ContextUsageRatio))]
    [NotifyPropertyChangedFor(nameof(ContextMeterWidth))]
    [NotifyPropertyChangedFor(nameof(ContextUsageText))]
    [NotifyPropertyChangedFor(nameof(ShowContextMeter))]
    [NotifyPropertyChangedFor(nameof(IsContextTight))]
    [NotifyPropertyChangedFor(nameof(ContextMeterBrush))]
    private long contextTokens;

    public double ContextUsageRatio => Math.Clamp(ContextTokens / (double)EstimatedContextWindow, 0, 1);

    /// <summary>
    /// 水位条的填充宽度。轨道固定 720（输入框 840 减去右侧百分比文字），
    /// 不用 ProgressBar：Arc 主题下 3px 高的进度条渲染不出来，自绘两段 Border 更可控。
    /// </summary>
    public double ContextMeterWidth => Math.Max(4, 720 * ContextUsageRatio);

    /// <summary>低于 40% 不显示：常态下多一条横线只会变成噪声。</summary>
    public bool ShowContextMeter => ContextTokens > EstimatedContextWindow * 0.4;

    /// <summary>超过 70% 提示会开始压缩早期内容。</summary>
    public bool IsContextTight => ContextTokens > EstimatedContextWindow * 0.7;

    public string ContextUsageText
        => $"{ContextTokens / 1000d:0.#}k / 128k · {ContextUsageRatio:P0}";

    public IBrush ContextMeterBrush => IsContextTight
        ? new SolidColorBrush(Color.Parse("#B3261E"))
        : new SolidColorBrush(Color.Parse("#8A8A82"));

    [RelayCommand]
    private void JumpToApproval()
    {
        FloatingHint = null;
        RequestScroll(true);
    }

    /// <summary>把这条会话导成 Markdown：粘进文档、发给同事都不用再排版。</summary>
    [RelayCommand]
    private async Task ExportAsync()
    {
        if (!HasMessages)
        {
            StatusText = "还没有内容可以导出";
            return;
        }

        var path = await _host.Picker.PickSaveFileAsync(
            "导出会话",
            SessionExport.SuggestFileName(Session.Title) + ".md",
            "md");

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await File.WriteAllTextAsync(path, SessionExport.ToMarkdown(Session), Encoding.UTF8);
            StatusText = "已导出";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText = "导出失败：" + ex.Message;
        }
    }

    // ---- 会话内查找 ----

    /// <summary>把某条消息滚进视野。视图负责找容器，VM 只说"是这一条"。</summary>
    public event EventHandler<MessageViewModel>? ScrollToMessageRequested;

    [ObservableProperty]
    private bool isSearchOpen;

    [ObservableProperty]
    private string searchQuery = string.Empty;

    /// <summary>「第 2 / 共 7 处」或「没有匹配」。</summary>
    [ObservableProperty]
    private string searchCounter = string.Empty;

    /// <summary>
    /// 命中的消息，按出现顺序排，同一条只算一次；右边记它在本条里出现了几次。
    /// 以前同一条消息命中两次就进两次列表，指针数到 7、屏幕上却只有一条变色。
    /// </summary>
    private readonly List<MessageViewModel> _searchHits = [];

    private readonly Dictionary<MessageViewModel, int> _searchHitCounts = [];

    private int _searchHitIndex = -1;

    partial void OnSearchQueryChanged(string? value) => RebuildSearchHits(value);

    [RelayCommand]
    private void OpenSearch()
    {
        IsSearchOpen = true;

        if (!string.IsNullOrWhiteSpace(SearchQuery))
        {
            RebuildSearchHits(SearchQuery);
        }
    }

    [RelayCommand]
    private void CloseSearch()
    {
        IsSearchOpen = false;
        SearchQuery = string.Empty;
        SearchCounter = string.Empty;
        _searchHits.Clear();
        _searchHitCounts.Clear();
        _searchHitIndex = -1;
        ClearSearchMarks();
        Composer.RequestFocus();
    }

    [RelayCommand]
    private void NextSearchHit() => MoveSearchHit(1);

    [RelayCommand]
    private void PreviousSearchHit() => MoveSearchHit(-1);

    private void RebuildSearchHits(string? query)
    {
        ClearSearchMarks();
        _searchHits.Clear();
        _searchHitCounts.Clear();
        _searchHitIndex = -1;

        var keyword = (query ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            SearchCounter = string.Empty;
            return;
        }

        foreach (var message in Messages)
        {
            var count = 0;
            foreach (var text in SearchableTexts(message))
            {
                count += CountOccurrences(text, keyword);
            }

            if (count == 0)
            {
                continue;
            }

            _searchHits.Add(message);
            _searchHitCounts[message] = count;
        }

        if (_searchHits.Count == 0)
        {
            SearchCounter = "没有匹配";
            return;
        }

        MoveSearchHit(1);
    }

    private static int CountOccurrences(string text, string keyword)
    {
        var count = 0;
        var index = 0;
        while (index < text.Length)
        {
            index = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            count++;
            index += keyword.Length;
        }

        return count;
    }

    private void MoveSearchHit(int delta)
    {
        if (_searchHits.Count == 0)
        {
            return;
        }

        var next = _searchHitIndex + delta;
        if (next < 0)
        {
            next = _searchHits.Count - 1;
        }
        else if (next >= _searchHits.Count)
        {
            next = 0;
        }

        _searchHitIndex = next;
        var target = _searchHits[next];

        foreach (var message in Messages)
        {
            message.IsSearchHit = ReferenceEquals(message, target);
        }

        var occurrences = _searchHitCounts.TryGetValue(target, out var value) ? value : 0;
        SearchCounter = $"第 {next + 1} 条 / 共 {_searchHits.Count} 条"
                        + (occurrences > 1 ? $" · 本条 {occurrences} 处" : string.Empty);

        ScrollToMessageRequested?.Invoke(this, target);
    }

    private void ClearSearchMarks()
    {
        foreach (var message in Messages)
        {
            message.IsSearchHit = false;
        }
    }

    /// <summary>能被搜到的东西：用户原话、助手正文、工具那一行和它的结果。</summary>
    private static IEnumerable<string> SearchableTexts(MessageViewModel message)
    {
        switch (message)
        {
            case UserMessageViewModel user when !string.IsNullOrWhiteSpace(user.Text):
                yield return user.Text;
                break;

            case AssistantMessageViewModel assistant:
                if (!string.IsNullOrWhiteSpace(assistant.Text))
                {
                    yield return assistant.Text;
                }

                foreach (var tool in assistant.Tools)
                {
                    if (!string.IsNullOrWhiteSpace(tool.Summary))
                    {
                        yield return tool.Summary;
                    }

                    if (!string.IsNullOrWhiteSpace(tool.Result))
                    {
                        yield return tool.Result!;
                    }
                }

                break;

            case NoticeMessageViewModel notice when !string.IsNullOrWhiteSpace(notice.Text):
                yield return notice.Text;
                break;
        }
    }


    public event EventHandler<bool>? ScrollToEndRequested;

    [ObservableProperty]
    private bool isBusy;

    [ObservableProperty]
    private string statusText = "就绪";

    [RelayCommand]
    private void Back() => _host.OpenHome();

    [RelayCommand]
    private void Stop()
    {
        _cts?.Cancel();
        _runtime.Abort(Session.Id);
        StatusText = "正在停止…";

        // 停止是明确的"不要继续"，排在后面的消息也不该再自己发出去。
        var dropped = DropQueuedPrompts();
        if (dropped > 0)
        {
            Messages.Add(new NoticeMessageViewModel($"已停止，排队的 {dropped} 条消息没有发送。"));
            RequestScroll(true);
        }
    }

    /// <summary>
    /// 关窗前调用：把当前流式输出的正文同步写回存档。
    /// 正常收尾在 RunPromptAsync 的 finally 里，但那个 finally 是异步的——进程退出不会等它。
    /// 这里先把已经生成的内容落到盘上，再让调用方去停。
    /// </summary>
    public void FlushPartialForShutdown()
    {
        if (_activeEntry is not { } entry)
        {
            return;
        }

        var assistant = _activeAssistant;
        if (assistant is not null)
        {
            entry.Text = assistant.Text;
            entry.Reasoning = assistant.Reasoning;
            entry.Model = assistant.Model ?? entry.Model;
            entry.InputTokens = assistant.InputTokens;
            entry.OutputTokens = assistant.OutputTokens;
        }

        // 这一轮确定跑不完了，存档里标成被停止：重开时不会显示成「完成」
        entry.Stopped = true;
        _flushedForShutdown = true;
        Session.UpdatedAt = DateTimeOffset.Now;
        _host.SaveSessionNow(Session);
    }

    /// <summary>生成期间按了发送：按先后顺序记下来，本轮结束后一条一条接着发。</summary>
    private void OnQueueRequested(object? sender, PromptRequest request)    {
        _queuedPrompts.Add(request);
        UpdateQueueHint();
    }

    private void OnQueueCancelled(object? sender, EventArgs e)
    {
        _queuedPrompts.Clear();
        UpdateQueueHint();
    }

    /// <summary>提示条上写清楚排了几条、最后一条是什么；只有一条时保持原来的写法。</summary>
    private void UpdateQueueHint()
    {
        Composer.QueuedCount = _queuedPrompts.Count;

        if (_queuedPrompts.Count == 0)
        {
            Composer.QueuedHint = null;
            return;
        }

        var preview = Preview(_queuedPrompts[^1].Text);
        Composer.QueuedHint = _queuedPrompts.Count == 1
            ? $"已排队：{preview}"
            : $"已排队 {_queuedPrompts.Count} 条 · 最后一条：{preview}";
    }

    private static string Preview(string text)
    {
        var flat = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        if (flat.Length == 0)
        {
            return "（图片）";
        }

        return flat.Length <= 24 ? flat : flat[..24] + "…";
    }

    /// <summary>丢掉全部排队内容，返回丢掉几条。</summary>
    private int DropQueuedPrompts()
    {
        var count = _queuedPrompts.Count;
        if (count == 0)
        {
            return 0;
        }

        _queuedPrompts.Clear();
        UpdateQueueHint();
        return count;
    }

    public async Task SendPromptAsync(string text)
        => await SendPromptAsync(PromptRequest.Plain(text));

    public async Task SendPromptAsync(PromptRequest request)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(request.Text))
        {
            return;
        }

        await RunPromptAsync(request with { Text = request.Text.Trim() }, addUserMessage: true);

        // 这一轮结束，按排队顺序接着发。已经删掉的任务不再往下跑。
        if (IsDiscarded || _queuedPrompts.Count == 0)
        {
            return;
        }

        var queued = _queuedPrompts[0];
        _queuedPrompts.RemoveAt(0);
        UpdateQueueHint();

        // 上一轮是失败结束的（401、断网），这时候再发也只会再失败一次，
        // 反而把用户排队的消息烧掉。全部还回输入框，让他决定。
        if (_lastRunFailed)
        {
            var restored = new List<PromptRequest> { queued };
            restored.AddRange(_queuedPrompts);
            _queuedPrompts.Clear();
            UpdateQueueHint();

            var text = string.Join(
                Environment.NewLine + Environment.NewLine,
                restored.Select(item => item.Text).Where(item => !string.IsNullOrWhiteSpace(item)));

            if (!string.IsNullOrWhiteSpace(text))
            {
                Composer.Text = string.IsNullOrWhiteSpace(Composer.Text)
                    ? text
                    : Composer.Text.TrimEnd() + Environment.NewLine + Environment.NewLine + text;
            }

            Messages.Add(new NoticeMessageViewModel($"上一轮没有跑完，排队的 {restored.Count} 条已放回输入框。"));
            RequestScroll(true);
            Composer.RequestFocus();
            return;
        }

        await SendPromptAsync(queued);
    }

    /// <summary>
    /// 需要用户点头的动作（目前只有「重跑会删掉后面几轮」）走这里。
    /// VM 不认识窗口，由 ChatView 把弹窗接上；没接的时候按「不确认」处理，宁可不删。
    /// </summary>
    public Func<string, string, string, bool, Task<bool>>? ConfirmAsync { get; set; }

    /// <summary>
    /// 从某条回复重来：截掉它和它后面的一切，用同一轮的用户提问重跑。
    /// 重试挂在回复下面，点哪条就从哪轮开始，而不是永远重跑最后一轮。
    /// </summary>
    private async Task RetryFromAsync(AssistantMessageViewModel message)
    {
        if (IsBusy)
        {
            return;
        }

        var messageIndex = Messages.IndexOf(message);
        if (messageIndex < 0)
        {
            return;
        }

        var userIndex = -1;
        for (var i = messageIndex; i >= 0; i--)
        {
            if (Messages[i] is UserMessageViewModel)
            {
                userIndex = i;
                break;
            }
        }

        if (userIndex < 0)
        {
            return;
        }

        // 只有「这条之后还有提问」时才会真的丢东西，这时候才拦一下。
        // 最后一条回复的重试是无损的，弹窗只会变成纯摩擦。
        var laterTurns = Messages.Skip(userIndex + 1).Count(item => item is UserMessageViewModel);
        if (laterTurns > 0)
        {
            if (ConfirmAsync is null)
            {
                return;
            }

            var confirmed = await ConfirmAsync(
                "从这里重跑？",
                $"会先删掉这条提问之后的 {laterTurns} 轮对话，再重新回答。删掉的内容不能恢复。",
                "重跑并删除",
                true);

            if (!confirmed)
            {
                return;
            }
        }

        var userMessage = (UserMessageViewModel)Messages[userIndex];
        var prompt = new PromptRequest(userMessage.Text, userMessage.Attachments, userMessage.SourceImages);

        // 用户消息和 Session.Entries 里的 User 条目严格一一对应（提示条只进消息流不进存档），
        // 所以按「第几轮提问」定位要截断的存档位置，比按消息下标找更稳。
        var turn = Messages.Take(userIndex + 1).Count(item => item is UserMessageViewModel) - 1;
        var entryIndex = -1;
        for (var i = 0; i < Session.Entries.Count; i++)
        {
            if (Session.Entries[i].Kind != ChatEntryKind.User)
            {
                continue;
            }

            if (turn-- == 0)
            {
                entryIndex = i;
                break;
            }
        }

        if (entryIndex < 0)
        {
            return;
        }

        if (Session.Entries.Count > entryIndex + 1)
        {
            Session.Entries.RemoveRange(entryIndex + 1, Session.Entries.Count - entryIndex - 1);
        }

        while (Messages.Count > userIndex + 1)
        {
            Messages.RemoveAt(Messages.Count - 1);
        }

        // 截到一条不剩时这条任务又变成「没开口」，工作区重新跟着选择走
        SyncComposerWorkspacePin();

        await RunPromptAsync(prompt, addUserMessage: false);
    }

    /// <summary>
    /// 接着一条被停掉的回复继续写：不新增用户消息、不新增气泡，让同一条回复往下长。
    /// 只允许最后一条——改中间那条会把后面的内容全冲掉。
    /// </summary>
    private async Task ContinueFromAsync(AssistantMessageViewModel message)
    {
        if (IsBusy || !message.IsStopped)
        {
            return;
        }

        if (!ReferenceEquals(LastMeaningfulMessage(), message))
        {
            return;
        }

        // 这句话不写进消息流，也不写进存档：用户没有真的提问，只是让模型接着写。
        var instruction = new PromptRequest(
            "接着上面没写完的部分继续，不要重复已经写过的内容，也不要重新开头。",
            []);

        await RunPromptAsync(instruction, addUserMessage: false, continuation: message);
    }

    /// <summary>
    /// 跑一轮。<paramref name="continuation"/> 非空表示「接着这条回复继续写」：
    /// 不新增用户消息、不新增气泡，把新内容追加到同一个 AssistantMessageViewModel 上。
    /// </summary>
    private async Task RunPromptAsync(PromptRequest input, bool addUserMessage, AssistantMessageViewModel? continuation = null)
    {
        IsBusy = true;
        Composer.IsBusy = true;
        StatusText = "正在准备…";

        // 这一轮用开始时的权限快照。生成中再切档位，要等到下一轮。
        _runStartPermission = _settings.Current.Permission;
        _pendingPermission = null;
        Composer.PermissionPendingNextRun = false;

        AssistantMessageViewModel? assistant = continuation;
        ChatEntry? assistantEntry = null;
        var stopReason = AgentStopReason.Completed;
        var failed = false;

        try
        {
            if (continuation is null)
            {
                if (addUserMessage)
                {
                    Session.Entries.Add(new ChatEntry
                    {
                        Kind = ChatEntryKind.User,
                        Text = input.Text,
                        Attachments = [.. input.Attachments],
                        Images = [.. input.Images ?? []],
                    });

                    Messages.Add(new UserMessageViewModel(input.Text, input.Attachments, input.Images));

                    if (Session.Entries.Count(entry => entry.Kind == ChatEntryKind.User) == 1)
                    {
                        Session.Title = SessionText.BuildTitle(input.Text);
                        OnPropertyChanged(nameof(Title));

                        // 第一句话落下来就把任务写进侧栏，不等这一轮跑完：
                        // 模型还在流的时候侧栏里本来什么都没有，用户会以为任务没建成；
                        // 而且整轮跑完才写文件的话，中途关掉应用这条任务就没了。
                        await _host.RegisterSessionAsync(Session);
                    }
                }

                assistantEntry = new ChatEntry
                {
                    Kind = ChatEntryKind.Assistant,
                    Model = _settings.Current.Model,
                };
                Session.Entries.Add(assistantEntry);

                assistant = new AssistantMessageViewModel
                {
                    ScrollRequested = () => RequestScroll(false),
                };
                // 重试要能定位到自己这一轮，所以先把实例建好再把回调挂上去
                assistant.RetryRequested = () => RetryFromAsync(assistant);
                assistant.ContinueRequested = () => ContinueFromAsync(assistant);
                Messages.Add(assistant);
            }
            else
            {
                // 继续写：沿用原来那条助手条目，跑完把整段文字一次性写回去，存档里仍是一条回复
                assistantEntry = Session.Entries.LastOrDefault(entry => entry.Kind == ChatEntryKind.Assistant);
                if (assistantEntry is null)
                {
                    // 理论上不会发生；真碰上了也别让工具事件往 null 上写
                    assistantEntry = new ChatEntry { Kind = ChatEntryKind.Assistant, Model = _settings.Current.Model };
                    Session.Entries.Add(assistantEntry);
                }

                assistant!.Resume();
            }

            // 记下这一轮的落点：关窗时用它把已经流出来的半截回复同步写进存档
            _activeEntry = assistantEntry;
            _activeAssistant = assistant;

            // addUserMessage=false（重试）时，最后一条也是这次要重发的 user，一并排除；
            // 继续写则保留半截回复，模型得看见自己写到哪儿了。
            var historyEntries = continuation is null
                ? Session.Entries.Take(Session.Entries.Count - 1).ToList()
                : Session.Entries.ToList();

            var request = new AgentRunRequest(
                Session.Id,
                BuildModelPrompt(input.Text, input.Attachments),
                BuildHistory(historyEntries),
                Session.Mode,
                _settings.Current.Permission,
                Session.WorkspacePath,
                new ChatApprovalGate(this),
                input.Images);

            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            await foreach (var @event in _runtime.RunAsync(request, _cts.Token))
            {
                switch (@event)
                {
                    case AgentStreamEvent.RunStarted started:
                        StatusText = $"正在连接 {started.Model}…";
                        assistant.Model = started.Model;
                        break;

                    case AgentStreamEvent.TextDelta delta:
                        assistant.AppendText(delta.Delta);
                        StatusText = "正在回复…";
                        if (!IsAtBottom)
                        {
                            FloatingHint = "有新内容，点这里回到底部";
                        }

                        break;

                    case AgentStreamEvent.ThinkingDelta thinking:
                        assistant.AppendReasoning(thinking.Delta);
                        StatusText = "正在思考…";
                        break;

                    case AgentStreamEvent.ToolStarted toolStarted:
                    {
                        var tool = new ToolCallViewModel(
                            toolStarted.CallId,
                            toolStarted.Name,
                            toolStarted.Label,
                            toolStarted.Arguments);
                        assistant.Tools.Add(tool);
                        assistantEntry.ToolCalls.Add(new ToolCallRecord
                        {
                            Id = toolStarted.CallId,
                            Name = toolStarted.Name,
                            Label = toolStarted.Label,
                            Arguments = toolStarted.Arguments,
                        });
                        StatusText = $"正在执行「{toolStarted.Label}」…";
                        RequestScroll(false);
                        break;
                    }

                    case AgentStreamEvent.ToolFinished toolFinished:
                    {
                        var tool = assistant.Tools.FirstOrDefault(item => item.Id == toolFinished.CallId);
                        tool?.Finish(toolFinished.Result, toolFinished.IsError, toolFinished.DurationMs);

                        var record = assistantEntry.ToolCalls.FirstOrDefault(item => item.Id == toolFinished.CallId);
                        if (record is not null)
                        {
                            record.Result = toolFinished.Result;
                            record.IsError = toolFinished.IsError;
                            record.DurationMs = toolFinished.DurationMs;
                        }

                        StatusText = toolFinished.IsError ? "工具执行失败，继续处理…" : "工具执行完成，继续处理…";
                        RequestScroll(false);
                        break;
                    }

                    case AgentStreamEvent.RunCompleted completed:
                    {
                        stopReason = completed.StopReason;
                        assistant.Model = completed.Model ?? assistant.Model;
                        assistant.InputTokens = completed.InputTokens;
                        assistant.OutputTokens = completed.OutputTokens;
                        ContextTokens = completed.InputTokens + completed.OutputTokens;
                        assistantEntry.Model = assistant.Model;
                        assistantEntry.InputTokens = completed.InputTokens;
                        assistantEntry.OutputTokens = completed.OutputTokens;

                        if (!string.IsNullOrWhiteSpace(completed.Error))
                        {
                            SetError(assistant, assistantEntry, completed.Error);
                            failed = true;
                        }

                        if (completed.StopReason == AgentStopReason.Aborted && string.IsNullOrWhiteSpace(assistant.Error))
                        {
                            // 用户按停不是故障：走中性提示，不套危险色。
                            assistant.IsStopped = true;
                            assistantEntry.Stopped = true;
                        }
                        else if (completed.StopReason == AgentStopReason.Length)
                        {
                            SetError(assistant, assistantEntry, "回复达到长度上限，已截断。");
                        }

                        break;
                    }

                    case AgentStreamEvent.RunFailed runFailed:
                        SetError(assistant, assistantEntry, runFailed.Message);
                        failed = true;
                        StatusText = "运行失败";
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            stopReason = AgentStopReason.Aborted;
            if (assistant is not null)
            {
                assistant.IsStopped = true;
            }
        }
        catch (Exception ex)
        {
            if (assistant is not null)
            {
                SetError(assistant, assistantEntry, ex.Message);
            }

            failed = true;
            StatusText = "运行失败";
        }
        finally
        {
            if (assistant is not null)
            {
                // 按停时 ToolFinished 不会再来。不在这里收尾的话，那一行会永远写着「运行中」，
                // 而用户刚按完停止，正盯着屏幕看它到底停没停。
                foreach (var tool in assistant.Tools.Where(item => item.IsRunning))
                {
                    tool.Abort();

                    var record = assistantEntry?.ToolCalls.FirstOrDefault(item => item.Id == tool.Id);
                    if (record is not null)
                    {
                        record.Aborted = true;
                        record.DurationMs = tool.DurationMs;
                    }
                }

                assistant.Complete();
                if (!assistant.HasText && !assistant.HasTools && assistant.Error is null)
                {
                    if (stopReason == AgentStopReason.Aborted)
                    {
                        assistant.IsStopped = true;
                    }
                    else
                    {
                        SetError(assistant, assistantEntry, "模型没有返回内容。");
                    }
                }
            }

            if (assistantEntry is not null)
            {
                assistantEntry.Text = assistant?.Text ?? string.Empty;
                assistantEntry.Reasoning = assistant?.Reasoning;
                assistantEntry.Error = assistant?.Error;
                assistantEntry.Stopped = _flushedForShutdown || (assistant?.IsStopped ?? false);
                assistantEntry.Model = assistant?.Model ?? assistantEntry.Model;
                assistantEntry.InputTokens = assistant?.InputTokens ?? 0;
                assistantEntry.OutputTokens = assistant?.OutputTokens ?? 0;
            }

            Session.UpdatedAt = DateTimeOffset.Now;
            _lastRunFailed = failed || !string.IsNullOrWhiteSpace(assistant?.Error);
            IsBusy = false;
            Composer.IsBusy = false;

            // 第一轮跑完，「还没开口」变成「钉在这个目录」：胶囊从此跟任务走，不再跟设置走
            SyncComposerWorkspacePin();

            // 生成中切过权限：这一轮按旧档位跑完了，现在把「已生效」补上。
            Composer.PermissionPendingNextRun = false;
            if (_pendingPermission is { } pending)
            {
                _pendingPermission = null;
                if (pending != _runStartPermission)
                {
                    Messages.Add(new NoticeMessageViewModel(PermissionNoticeText(pending)));
                    RequestScroll(true);
                }
            }

            StatusText = failed
                ? "运行失败"
                : stopReason == AgentStopReason.Aborted ? "已停止" : "完成";
            if (IsAtBottom)
            {
                FloatingHint = null;
            }

            RequestScroll(false);

            // 任务已经被删了就别再写回去，否则删掉的任务会自己回来
            if (IsDiscarded)
            {
                StatusText = "已删除";
            }
            else
            {
                try
                {
                    await _host.SaveSessionAsync(Session);
                }
                catch (Exception ex)
                {
                    StatusText = "会话保存失败：" + ex.Message;
                }
            }

            // 这一轮已经完整收尾，关窗时不用再抢救了
            _activeEntry = null;
            _activeAssistant = null;
            _flushedForShutdown = false;
        }
    }

    /// <summary>仅用于 --screenshot：摆一张待确认的授权卡，检查快捷键提示和按钮版式。</summary>
    public void AddDemoApproval()
    {
        Messages.Add(new ApprovalMessageViewModel(
            "run_command",
            "执行命令",
            "{\n  \"command\": \"dotnet build\"\n}",
            "run_command 会用 shell 执行命令，需要你确认。",
            _ => { }));

        RequestScroll(true);
    }

    private void LoadEntries()
    {
        foreach (var entry in Session.Entries)
        {
            switch (entry.Kind)
            {
                case ChatEntryKind.User:
                    ResolveAttachmentPaths(entry);
                    Messages.Add(new UserMessageViewModel(entry.Text, entry.Attachments, entry.Images) { CreatedAt = entry.CreatedAt });
                    break;

                case ChatEntryKind.Assistant:
                {
                    var assistant = new AssistantMessageViewModel
                    {
                        CreatedAt = entry.CreatedAt,
                        Model = entry.Model,
                        InputTokens = entry.InputTokens,
                        OutputTokens = entry.OutputTokens,
                        Error = entry.Error,
                        ScrollRequested = () => RequestScroll(false),
                    };
                    assistant.RetryRequested = () => RetryFromAsync(assistant);
                    assistant.ContinueRequested = () => ContinueFromAsync(assistant);
                    assistant.SetPersisted(entry.Text, entry.Reasoning);
                    assistant.IsStopped = entry.Stopped;

                    // 存档里只有原始报错，分类和按钮要在这里补回来
                    if (!string.IsNullOrWhiteSpace(entry.Error))
                    {
                        SetError(assistant, null, entry.Error!);
                    }

                    foreach (var record in entry.ToolCalls)
                    {
                        var tool = new ToolCallViewModel(record.Id, record.Name, record.Label, record.Arguments);
                        tool.Finish(record.Result, record.IsError, record.DurationMs);

                        // 重新打开会话时也要记得这一轮是被停掉的，不能显示成「完成」
                        if (record.Aborted)
                        {
                            tool.MarkAborted(record.DurationMs);
                        }

                        assistant.Tools.Add(tool);
                    }

                    Messages.Add(assistant);
                    break;
                }

                case ChatEntryKind.Notice:
                    Messages.Add(new NoticeMessageViewModel(entry.Text));
                    break;
            }
        }

        // 打开旧会话时把水位接上，别让用户以为这条会话是空的
        ContextTokens = Session.Entries.LastOrDefault(entry => entry.Kind == ChatEntryKind.Assistant) is { } latest
            ? latest.InputTokens + latest.OutputTokens
            : 0;
    }

    /// <summary>
    /// 旧存档只记了相对路径。打开会话时补一次绝对路径，
    /// 否则「在资源管理器中显示」只能靠当前进程目录去猜。
    /// </summary>
    private void ResolveAttachmentPaths(ChatEntry entry)
    {
        if (entry.Attachments.Count == 0)
        {
            return;
        }

        foreach (var attachment in entry.Attachments)
        {
            if (!string.IsNullOrWhiteSpace(attachment.FullPath) || string.IsNullOrWhiteSpace(attachment.Path))
            {
                continue;
            }

            var candidate = Path.IsPathRooted(attachment.Path)
                ? attachment.Path
                : Path.Combine(PlainWorkspace.Resolve(Session.WorkspacePath), attachment.Path);

            if (File.Exists(candidate))
            {
                attachment.FullPath = candidate;
            }
        }
    }

    private static IReadOnlyList<HistoryTurn> BuildHistory(IEnumerable<ChatEntry> entries)    {
        var history = new List<HistoryTurn>();
        foreach (var entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry.Text))
            {
                continue;
            }

            if (entry.Kind == ChatEntryKind.User)
            {
                // 历史轮次不再重复注入工作模式：模式只对这一次提问生效，
                // 每轮都塞一遍会白烧 token，还容易让模型以为模式中途变过。
                history.Add(HistoryTurn.User(PromptComposer.Compose(entry.Text, entry.Attachments)));
            }
            else if (entry.Kind == ChatEntryKind.Assistant)
            {
                history.Add(HistoryTurn.Assistant(entry.Text));
            }
        }

        return history;
    }

    /// <summary>
    /// 把引用的资料拼进发给模型的文本。气泡里只显示用户原话——
    /// 几千字的文档塞进用户消息，界面就没法看了。
    /// </summary>
    private string BuildModelPrompt(string text, IReadOnlyList<ChatAttachment> attachments)
        => PromptComposer.Compose(text, attachments, mode: Session.Mode);

    private void RequestScroll(bool force) => ScrollToEndRequested?.Invoke(this, force);

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasMessages));
        OnPropertyChanged(nameof(ShowEmptyState));
        RefreshContinueAvailability();
        RefreshDateHeaders();

        var addedUserMessage = e.NewItems?.OfType<UserMessageViewModel>().Any() == true;
        RequestScroll(addedUserMessage);

        // 不在这里重算命中：回复流式输出时每加一张工具卡都重算，会把用户正在看的那一处
        // 拽回第一处。查找结果按用户按下的一刻为准，改关键词才刷新。
    }

    /// <summary>
    /// 「继续」只挂在最后一条消息上，而且这条必须是助手回复：
    /// 后面已经有新提问（或别的条目）时，接着写会把后面的内容冲掉。
    /// 权限变更那类提示不算「后文」，它们只是插在后面的说明，不影响继续。
    /// </summary>
    private void RefreshContinueAvailability()
    {
        var last = LastMeaningfulMessage() as AssistantMessageViewModel;
        foreach (var message in Messages.OfType<AssistantMessageViewModel>())
        {
            message.IsLatest = ReferenceEquals(message, last);
        }
    }

    /// <summary>跳过只有提示性质的系统消息，找真正的内容尾巴。</summary>
    private MessageViewModel? LastMeaningfulMessage()
    {
        for (var index = Messages.Count - 1; index >= 0; index--)
        {
            if (Messages[index] is not NoticeMessageViewModel)
            {
                return Messages[index];
            }
        }

        return null;
    }

    /// <summary>
    /// 失败时不要只丢一段原文：分成几类，给一句人话和一个真的能解决问题的按钮。
    /// </summary>
    private void SetError(AssistantMessageViewModel message, ChatEntry? entry, string raw)
    {
        message.Error = raw;
        if (entry is not null)
        {
            entry.Error = raw;
        }

        var info = RunErrorInfo.Classify(raw);
        message.ErrorTitle = info.Title;
        message.ErrorHint = info.Hint;
        message.ErrorActionLabel = info.ActionLabel;
        message.ErrorActionKind = info.Kind;
        message.ErrorActionRequested = () => HandleErrorActionAsync(message);
    }

    private async Task HandleErrorActionAsync(AssistantMessageViewModel message)
    {
        switch (message.ErrorActionKind)
        {
            case RunErrorKind.Auth:
            case RunErrorKind.ModelNotFound:
            case RunErrorKind.Quota:
            case RunErrorKind.LengthLimit:
                await _host.OpenSettingsAsync();
                break;

            case RunErrorKind.ContextTooLong:
                _host.NewTaskCommand.Execute(null);
                break;

            default:
                await RetryFromAsync(message);
                break;
        }
    }

    /// <summary>
    /// 跨天的消息之间插一条日期。只是给消息打个标记，不往 Messages 里加条目——
    /// 消息和存档条目一一对应，多一个条目会把重试的轮次定位算错。
    /// </summary>
    private void RefreshDateHeaders()
    {
        DateTime? previous = null;
        foreach (var message in Messages)
        {
            var date = message.CreatedAt.LocalDateTime.Date;
            var show = previous != date;
            message.ShowDateHeader = show;
            if (show)
            {
                message.DateHeaderText = DescribeDate(date);
            }

            previous = date;
        }
    }

    /// <summary>
    /// 用键盘回答最后一张待审批卡。返回 false 表示当前没有待确认的卡片，
    /// 按键应该继续交回输入框，别把 Esc 吞掉。
    /// </summary>
    public bool TryAnswerApproval(bool allow, bool always)
    {
        var card = Messages.OfType<ApprovalMessageViewModel>().LastOrDefault(item => item.ShowActions);
        if (card is null)
        {
            return false;
        }

        if (!allow)
        {
            card.DenyCommand.Execute(null);
        }
        else if (always)
        {
            card.AllowAlwaysCommand.Execute(null);
        }
        else
        {
            card.AllowCommand.Execute(null);
        }

        return true;
    }

    private static string DescribeDate(DateTime date)
    {
        var today = DateTime.Today;
        if (date == today)
        {
            return "今天";
        }

        if (date == today.AddDays(-1))
        {
            return "昨天";
        }

        var weekday = date.DayOfWeek switch
        {
            DayOfWeek.Monday => "周一",
            DayOfWeek.Tuesday => "周二",
            DayOfWeek.Wednesday => "周三",
            DayOfWeek.Thursday => "周四",
            DayOfWeek.Friday => "周五",
            DayOfWeek.Saturday => "周六",
            _ => "周日",
        };

        return $"{date:M月d日} {weekday}";
    }

    private sealed class ChatApprovalGate(ChatViewModel owner) : IToolApprovalGate
    {
        public async Task<bool> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
        {
            // 完全访问在运行时那一层已经放行；这里再兜一次，避免会话复用了旧权限的闭包
            // 又把确认卡片弹出来。
            if (PermissionModeText.SkipsApproval(owner._settings.Current.Permission))
            {
                return true;
            }

            // 这一轮里用户点过「本会话都允许」，同一个工具不再问第二遍。
            if (owner._sessionAllowedTools.Contains(request.ToolName))
            {
                return true;
            }

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            ApprovalMessageViewModel? card = null;

            await Ui.OnUiAsync(() =>
            {
                card = new ApprovalMessageViewModel(
                    request.ToolName,
                    request.Label,
                    request.Arguments,
                    request.Reason,
                    allowed => completion.TrySetResult(allowed));
                card.AllowAlwaysRequested = () =>
                {
                    owner._sessionAllowedTools.Add(request.ToolName);
                    owner.Composer.SessionAllowedCount = owner._sessionAllowedTools.Count;
                };
                owner.Messages.Add(card);
                owner.PendingApprovalText = $"等待你确认：{request.Label}";

                // 用户正在上面读旧内容时不要抢滚动条，只在底部飘一句提示；
                // 停在底部就照常跟到卡片。
                owner.RequestScroll(owner.IsAtBottom);
                if (!owner.IsAtBottom)
                {
                    owner.FloatingHint = "有操作等待你确认，点这里查看";
                }
            });

            using var registration = cancellationToken.Register(() => completion.TrySetResult(false));
            var allowed = await completion.Task;

            if (card is not null)
            {
                await Ui.OnUiAsync(card.Cancel);
                await Ui.OnUiAsync(() => owner.PendingApprovalText = null);
            }

            return allowed;
        }
    }
}
