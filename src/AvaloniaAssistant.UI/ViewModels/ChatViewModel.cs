using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed partial class ChatViewModel : ObservableObject
{
    private readonly MainWindowViewModel _host;
    private readonly IAssistantRuntime _runtime;
    private readonly ISettingsProvider _settings;
    private CancellationTokenSource? _cts;

    public ChatViewModel(MainWindowViewModel host, ChatSession? session)
    {
        _host = host;
        _runtime = host.Runtime;
        _settings = host.SettingsProvider;

        Session = session ?? new ChatSession
        {
            WorkspacePath = host.Settings.WorkspacePath,
            Mode = host.Settings.Mode,
        };

        Composer = new ComposerViewModel(_runtime, _settings, host.Picker, host.OpenSettingsAsync);
        Composer.SendRequested += async (_, text) => await SendPromptAsync(text);
        Composer.StopRequested += (_, _) => Stop();

        Messages.CollectionChanged += OnMessagesChanged;
        _settings.Changed += (_, _) => OnPropertyChanged(nameof(HeaderSubtitle));

        LoadEntries();
    }

    public ChatSession Session { get; }

    public ObservableCollection<MessageViewModel> Messages { get; } = [];

    public ComposerViewModel Composer { get; }

    public string Title => Session.Title;

    public string HeaderSubtitle
    {
        get
        {
            var workspace = string.IsNullOrWhiteSpace(Session.WorkspacePath)
                ? "未设置工作区"
                : Path.GetFileName(Session.WorkspacePath.TrimEnd(Path.DirectorySeparatorChar));
            return $"{workspace} · {_settings.Current.Model}";
        }
    }

    public bool HasMessages => Messages.Count > 0;

    public event EventHandler? ScrollToEndRequested;

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
    }

    public async Task SendPromptAsync(string text)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        await RunPromptAsync(text.Trim(), addUserMessage: true);
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var index = Session.Entries.FindLastIndex(entry => entry.Kind == ChatEntryKind.User);
        if (index < 0)
        {
            return;
        }

        var prompt = Session.Entries[index].Text;
        if (Session.Entries.Count > index + 1)
        {
            Session.Entries.RemoveRange(index + 1, Session.Entries.Count - index - 1);
        }

        var lastUserMessage = Messages.LastOrDefault(message => message is UserMessageViewModel);
        if (lastUserMessage is not null)
        {
            var messageIndex = Messages.IndexOf(lastUserMessage);
            while (Messages.Count > messageIndex + 1)
            {
                Messages.RemoveAt(Messages.Count - 1);
            }
        }

        await RunPromptAsync(prompt, addUserMessage: false);
    }

    private async Task RunPromptAsync(string prompt, bool addUserMessage)
    {
        IsBusy = true;
        Composer.IsBusy = true;
        StatusText = "正在准备…";

        AssistantMessageViewModel? assistant = null;
        ChatEntry? assistantEntry = null;
        var stopReason = AgentStopReason.Completed;

        try
        {
            if (addUserMessage)
            {
                Session.Entries.Add(new ChatEntry
                {
                    Kind = ChatEntryKind.User,
                    Text = prompt,
                });

                Messages.Add(new UserMessageViewModel(prompt));

                if (Session.Entries.Count(entry => entry.Kind == ChatEntryKind.User) == 1)
                {
                    Session.Title = SessionText.BuildTitle(prompt);
                    OnPropertyChanged(nameof(Title));
                }
            }

            var historyEntries = addUserMessage
                ? Session.Entries.Take(Session.Entries.Count - 1)
                : Session.Entries;

            assistantEntry = new ChatEntry
            {
                Kind = ChatEntryKind.Assistant,
                Model = _settings.Current.Model,
            };
            Session.Entries.Add(assistantEntry);

            assistant = new AssistantMessageViewModel
            {
                RetryRequested = () => _ = RetryAsync(),
                ScrollRequested = RequestScroll,
            };
            Messages.Add(assistant);

            var request = new AgentRunRequest(
                Session.Id,
                prompt,
                BuildHistory(historyEntries),
                Session.Mode,
                _settings.Current.Permission,
                Session.WorkspacePath,
                new ChatApprovalGate(this));

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
                        RequestScroll();
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
                        RequestScroll();
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
                            assistant.Error = completed.Error;
                            assistantEntry.Error = completed.Error;
                        }

                        if (completed.StopReason == AgentStopReason.Aborted && string.IsNullOrWhiteSpace(assistant.Error))
                        {
                            assistant.Error = "已停止生成。";
                            assistantEntry.Error = assistant.Error;
                        }
                        else if (completed.StopReason == AgentStopReason.Length)
                        {
                            assistant.Error = "回复达到长度上限，已截断。";
                            assistantEntry.Error = assistant.Error;
                        }

                        break;
                    }

                    case AgentStreamEvent.RunFailed failed:
                        assistant.Error = failed.Message;
                        assistantEntry.Error = failed.Message;
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
                assistant.Error ??= "已停止生成。";
            }
        }
        catch (Exception ex)
        {
            if (assistant is not null)
            {
                assistant.Error = ex.Message;
            }

            StatusText = "运行失败";
        }
        finally
        {
            if (assistant is not null)
            {
                assistant.Complete();
                if (!assistant.HasText && !assistant.HasTools && assistant.Error is null)
                {
                    assistant.Error = stopReason == AgentStopReason.Aborted ? "已停止生成。" : "模型没有返回内容。";
                }
            }

            if (assistantEntry is not null)
            {
                assistantEntry.Text = assistant?.Text ?? string.Empty;
                assistantEntry.Reasoning = assistant?.Reasoning;
                assistantEntry.Error = assistant?.Error;
                assistantEntry.Model = assistant?.Model ?? assistantEntry.Model;
                assistantEntry.InputTokens = assistant?.InputTokens ?? 0;
                assistantEntry.OutputTokens = assistant?.OutputTokens ?? 0;
            }

            Session.UpdatedAt = DateTimeOffset.Now;
            IsBusy = false;
            Composer.IsBusy = false;
            StatusText = stopReason == AgentStopReason.Aborted ? "已停止" : "完成";
            RequestScroll();

            await _host.SaveSessionAsync(Session);
        }
    }

    private void LoadEntries()
    {
        foreach (var entry in Session.Entries)
        {
            switch (entry.Kind)
            {
                case ChatEntryKind.User:
                    Messages.Add(new UserMessageViewModel(entry.Text) { CreatedAt = entry.CreatedAt });
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
                        RetryRequested = () => _ = RetryAsync(),
                        ScrollRequested = RequestScroll,
                    };
                    assistant.SetPersisted(entry.Text, entry.Reasoning);

                    foreach (var record in entry.ToolCalls)
                    {
                        var tool = new ToolCallViewModel(record.Id, record.Name, record.Label, record.Arguments);
                        if (!string.IsNullOrWhiteSpace(record.Result) || record.DurationMs is not null || record.IsError)
                        {
                            tool.Finish(record.Result, record.IsError, record.DurationMs);
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
                history.Add(HistoryTurn.User(entry.Text));
            }
            else if (entry.Kind == ChatEntryKind.Assistant)
            {
                history.Add(HistoryTurn.Assistant(entry.Text));
            }
        }

        return history;
    }

    private void RequestScroll() => ScrollToEndRequested?.Invoke(this, EventArgs.Empty);

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasMessages));
        RequestScroll();
    }

    private sealed class ChatApprovalGate(ChatViewModel owner) : IToolApprovalGate
    {
        public async Task<bool> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
        {
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
                owner.Messages.Add(card);
                owner.RequestScroll();
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
