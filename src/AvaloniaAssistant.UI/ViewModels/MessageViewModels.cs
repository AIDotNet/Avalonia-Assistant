using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaAssistant.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public abstract class MessageViewModel : ObservableObject
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string TimeText => CreatedAt.ToLocalTime().ToString("HH:mm");
}

public sealed partial class UserMessageViewModel : MessageViewModel
{
    public UserMessageViewModel(string text)
    {
        Text = text;
    }

    public string Text { get; }
}

public sealed partial class ToolCallViewModel : ObservableObject
{
    public ToolCallViewModel(string id, string name, string label, string arguments)
    {
        Id = id;
        Name = name;
        Label = label;
        Arguments = arguments;
    }

    public string Id { get; }

    public string Name { get; }

    public string Label { get; }

    [ObservableProperty]
    private string arguments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private bool isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private long? durationMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    private bool isRunning = true;

    [ObservableProperty]
    private bool isExpanded;

    public string StatusText => IsRunning ? "运行中" : IsError ? "失败" : "完成";

    public IBrush StatusBrush => IsRunning
        ? new SolidColorBrush(Color.Parse("#8A8A82"))
        : IsError
            ? new SolidColorBrush(Color.Parse("#B3261E"))
            : new SolidColorBrush(Color.Parse("#2E7D32"));

    public string DurationText => DurationMs is null ? string.Empty : $"{DurationMs} ms";

    public bool HasResult => !string.IsNullOrWhiteSpace(Result);

    [RelayCommand]
    private void Toggle() => IsExpanded = !IsExpanded;

    public void Finish(string? result, bool isError, long? durationMs)
    {
        Result = result;
        IsError = isError;
        DurationMs = durationMs;
        IsRunning = false;
        if (isError)
        {
            IsExpanded = true;
        }
    }
}

public sealed partial class AssistantMessageViewModel : MessageViewModel
{
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _reasoning = new();
    private DispatcherTimer? _flushTimer;

    public AssistantMessageViewModel()
    {
        Tools.CollectionChanged += OnToolsChanged;
    }

    public ObservableCollection<ToolCallViewModel> Tools { get; } = [];

    public Action? RetryRequested { get; set; }

    public Action? ScrollRequested { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasText))]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    [NotifyPropertyChangedFor(nameof(ShowTyping))]
    private string text = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReasoning))]
    private string reasoning = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? error;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    [NotifyPropertyChangedFor(nameof(ShowTyping))]
    private bool isStreaming = true;

    [ObservableProperty]
    private string? model;

    [ObservableProperty]
    private bool isReasoningOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenText))]
    [NotifyPropertyChangedFor(nameof(HasTokens))]
    private long inputTokens;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TokenText))]
    [NotifyPropertyChangedFor(nameof(HasTokens))]
    private long outputTokens;

    public bool HasText => !string.IsNullOrWhiteSpace(Text);

    public bool HasReasoning => !string.IsNullOrWhiteSpace(Reasoning);

    public bool HasError => !string.IsNullOrWhiteSpace(Error);

    public bool HasTools => Tools.Count > 0;

    public bool HasTokens => InputTokens > 0 || OutputTokens > 0;

    public bool ShowTyping => IsStreaming && !HasText && !HasTools;

    public bool ShowActions => !IsStreaming && HasText;

    public string TokenText => $"输入 {Format(InputTokens)} · 输出 {Format(OutputTokens)}";

    public void AppendText(string delta)
    {
        _text.Append(delta);
        EnsureTimer();
    }

    public void AppendReasoning(string delta)
    {
        _reasoning.Append(delta);
        EnsureTimer();
    }

    public void Complete()
    {
        IsStreaming = false;
        Flush();
        _flushTimer?.Stop();
        _flushTimer = null;
    }

    public void SetPersisted(string text, string? reasoning)
    {
        _text.Clear();
        _text.Append(text);
        _reasoning.Clear();
        _reasoning.Append(reasoning ?? string.Empty);
        Text = text;
        Reasoning = reasoning ?? string.Empty;
        IsStreaming = false;
    }

    [RelayCommand]
    private void ToggleReasoning() => IsReasoningOpen = !IsReasoningOpen;

    [RelayCommand]
    private async Task CopyAsync() => await Services.Ui.CopyTextAsync(Text);

    [RelayCommand]
    private void Retry() => RetryRequested?.Invoke();

    private void EnsureTimer()
    {
        if (_flushTimer is not null)
        {
            return;
        }

        _flushTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(55) };
        _flushTimer.Tick += (_, _) => Flush();
        _flushTimer.Start();
    }

    private void Flush()
    {
        var text = _text.ToString();
        if (Text != text)
        {
            Text = text;
        }

        var reasoning = _reasoning.ToString();
        if (Reasoning != reasoning)
        {
            Reasoning = reasoning;
        }

        ScrollRequested?.Invoke();
    }

    private void OnToolsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(HasTools));
        OnPropertyChanged(nameof(ShowTyping));
    }

    private static string Format(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}k",
        _ => value.ToString(),
    };
}

public sealed partial class ApprovalMessageViewModel : MessageViewModel
{
    private readonly Action<bool> _resolve;

    public ApprovalMessageViewModel(string toolName, string label, string arguments, string reason, Action<bool> resolve)
    {
        ToolName = toolName;
        Label = label;
        Arguments = arguments;
        Reason = reason;
        _resolve = resolve;
    }

    public string ToolName { get; }

    public string Label { get; }

    public string Arguments { get; }

    public string Reason { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    private bool isResolved;

    [ObservableProperty]
    private string resolutionText = string.Empty;

    public bool ShowActions => !IsResolved;

    [RelayCommand]
    private void Allow() => Resolve(true);

    [RelayCommand]
    private void Deny() => Resolve(false);

    public void Resolve(bool allowed)
    {
        if (IsResolved)
        {
            return;
        }

        IsResolved = true;
        ResolutionText = allowed ? "已允许" : "已拒绝";
        _resolve(allowed);
    }

    public void Cancel()
    {
        if (IsResolved)
        {
            return;
        }

        IsResolved = true;
        ResolutionText = "已取消";
        _resolve(false);
    }
}

public sealed class NoticeMessageViewModel : MessageViewModel
{
    public NoticeMessageViewModel(string text, bool isError = false)
    {
        Text = text;
        IsError = isError;
    }

    public string Text { get; }

    public bool IsError { get; }
}
