using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
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

    /// <summary>生成期间按 Enter 排进来的那条，本轮结束后自动发出。只留一条，后到的替换先到的。</summary>
    private PromptRequest? _queuedPrompt;

    /// <summary>上一轮是不是失败结束的。失败时排队的消息不再往下发，先还回输入框。</summary>
    private bool _lastRunFailed;

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
    }

    public ChatSession Session { get; }

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

    private void OnSettingsChanged(object? sender, EventArgs e)
        => OnPropertyChanged(nameof(HeaderSubtitle));

    /// <summary>
    /// 权限档位变了就在对话里留一条提示：用户得清楚接下来的命令还会不会问自己。
    /// 只进消息流，不写进会话存档，重新打开不会重复出现。
    /// </summary>
    private void OnPermissionChanged(object? sender, PermissionMode permission)
    {
        if (IsBusy)
        {
            return;
        }

        var text = permission switch
        {
            PermissionMode.FullAccess => "已切换为完全访问：执行命令、修改文件都不再逐次确认。",
            PermissionMode.ReadOnly => "已切换为只读：不会修改文件，也不会执行命令。",
            _ => "已切换为默认权限：执行命令、调用 MCP 工具前会先问你。",
        };

        Messages.Add(new NoticeMessageViewModel(text));
        RequestScroll(true);
    }

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

    /// <summary>每一处命中落在哪条消息上。同一条消息里命中两次就是两条记录。</summary>
    private readonly List<MessageViewModel> _searchHits = [];

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
        _searchHitIndex = -1;

        var keyword = (query ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            SearchCounter = string.Empty;
            return;
        }

        foreach (var message in Messages)
        {
            foreach (var text in SearchableTexts(message))
            {
                var index = 0;
                while (index < text.Length)
                {
                    index = text.IndexOf(keyword, index, StringComparison.OrdinalIgnoreCase);
                    if (index < 0)
                    {
                        break;
                    }

                    _searchHits.Add(message);
                    index += keyword.Length;
                }
            }
        }

        if (_searchHits.Count == 0)
        {
            SearchCounter = "没有匹配";
            return;
        }

        MoveSearchHit(1);
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

        SearchCounter = $"第 {next + 1} / {_searchHits.Count} 处";
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

        // 停止是明确的"不要继续"，排在后面的那条也不该再自己发出去。
        if (DropQueuedPrompt())
        {
            Messages.Add(new NoticeMessageViewModel("已停止，排队的那条消息没有发送。"));
            RequestScroll(true);
        }
    }

    /// <summary>生成期间按了发送：记下来，本轮结束后接着发。</summary>
    private void OnQueueRequested(object? sender, PromptRequest request)
    {
        _queuedPrompt = request;

        var preview = request.Text.ReplaceLineEndings(" ").Trim();
        if (preview.Length > 28)
        {
            preview = preview[..28] + "…";
        }

        Composer.QueuedHint = $"已排队：{preview}";
    }

    private void OnQueueCancelled(object? sender, EventArgs e) => _queuedPrompt = null;

    /// <summary>丢掉排队内容，返回刚才是否有东西被丢掉。</summary>
    private bool DropQueuedPrompt()
    {
        if (_queuedPrompt is null)
        {
            return false;
        }

        _queuedPrompt = null;
        Composer.QueuedHint = null;
        return true;
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

        // 这一轮结束，把排队的那条接着发。已经删掉的任务不再往下跑。
        if (IsDiscarded || _queuedPrompt is not { } queued)
        {
            return;
        }

        _queuedPrompt = null;
        Composer.QueuedHint = null;

        // 上一轮是失败结束的（401、断网），这时候再发也只会再失败一次，
        // 反而把用户排队的那句话烧掉。先还回输入框，让他决定。
        if (_lastRunFailed)
        {
            Composer.Text = string.IsNullOrWhiteSpace(Composer.Text)
                ? queued.Text
                : Composer.Text.TrimEnd() + Environment.NewLine + Environment.NewLine + queued.Text;

            Messages.Add(new NoticeMessageViewModel("上一轮没有跑完，排队的那条已放回输入框。"));
            RequestScroll(true);
            Composer.RequestFocus();
            return;
        }

        await SendPromptAsync(queued);
    }

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
                assistantEntry.Stopped = assistant?.IsStopped ?? false;
                assistantEntry.Model = assistant?.Model ?? assistantEntry.Model;
                assistantEntry.InputTokens = assistant?.InputTokens ?? 0;
                assistantEntry.OutputTokens = assistant?.OutputTokens ?? 0;
            }

            Session.UpdatedAt = DateTimeOffset.Now;
            _lastRunFailed = failed;
            IsBusy = false;
            Composer.IsBusy = false;
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
        }
    }

    private void LoadEntries()
    {
        foreach (var entry in Session.Entries)
        {
            switch (entry.Kind)
            {
                case ChatEntryKind.User:
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
    }

    private static IReadOnlyList<HistoryTurn> BuildHistory(IEnumerable<ChatEntry> entries)
    {
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
        RefreshContinueAvailability();

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
            }

            return allowed;
        }
    }
}
