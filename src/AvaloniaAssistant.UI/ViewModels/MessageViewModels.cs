using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
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
    public ToolCallViewModel(string id, string name, string label, string arguments, string? workspacePath = null)
    {
        Id = id;
        Name = name;
        Label = label;
        Arguments = arguments;
        OutputPath = ReadOutputPath(name, arguments, workspacePath);
    }

    public string Id { get; }

    public string Name { get; }

    public string Label { get; }

    /// <summary>
    /// write_file 写到哪儿。卡片上给一个「打开文件」，省得照着参数里的路径去资源管理器里翻。
    /// 只认自己实现的那个工具，别的工具的参数名没有保证。
    /// </summary>
    public string? OutputPath { get; }

    public bool HasOutputPath => !string.IsNullOrEmpty(OutputPath);

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

    [RelayCommand]
    private void OpenOutput()
    {
        if (!string.IsNullOrEmpty(OutputPath))
        {
            Services.Ui.OpenExternal(OutputPath);
        }
    }

    [RelayCommand]
    private async Task CopyOutputPathAsync()
    {
        if (!string.IsNullOrEmpty(OutputPath))
        {
            await Services.Ui.CopyTextAsync(OutputPath);
        }
    }

    private static string? ReadOutputPath(string toolName, string arguments, string? workspacePath)
    {
        if (toolName != "write_file")
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (!document.RootElement.TryGetProperty("path", out var path)
                || path.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = path.GetString();
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            // write_file 的相对路径是按工作区解析的（见 ToolPaths.Resolve），
            // 直接拿原值去打开会落到应用自己的当前目录，文件根本不在那儿。
            if (Path.IsPathRooted(value) || string.IsNullOrWhiteSpace(workspacePath))
            {
                return value;
            }

            return Path.GetFullPath(Path.Combine(workspacePath, value));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

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

    public Func<Task>? RetryRequested { get; set; }

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
    [NotifyPropertyChangedFor(nameof(ShowActions))]
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

    /// <summary>
    /// 只有回复真的结束了才挂操作菜单。失败、被中断、或者整轮只调了工具没出文字时，
    /// 用户更需要「重试」，所以不把没有正文当成不显示的理由。
    /// </summary>
    public bool ShowActions => !IsStreaming && (HasText || HasError || HasTools);

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
    private Task RetryAsync() => RetryRequested?.Invoke() ?? Task.CompletedTask;

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
        OnPropertyChanged(nameof(ShowActions));
    }

    private static string Format(long value) => value switch
    {
        >= 1_000_000 => $"{value / 1_000_000d:0.#}M",
        >= 1_000 => $"{value / 1_000d:0.#}k",
        _ => value.ToString(),
    };
}

/// <summary>授权卡的三种结果。「本会话都允许」只对当前对话、当前工具生效。</summary>
public enum ApprovalChoice
{
    AllowOnce,
    AllowAlways,
    Deny,
}

public sealed partial class ApprovalMessageViewModel : MessageViewModel
{
    private readonly Action<ApprovalChoice> _resolve;

    public ApprovalMessageViewModel(
        string toolName,
        string label,
        string arguments,
        string reason,
        Action<ApprovalChoice> resolve)
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
    private void Allow() => Resolve(ApprovalChoice.AllowOnce);

    /// <summary>本对话里同一个工具不再逐次询问。仅内存生效，重新打开任务会重新问。</summary>
    [RelayCommand]
    private void AllowAlways() => Resolve(ApprovalChoice.AllowAlways);

    [RelayCommand]
    private void Deny() => Resolve(ApprovalChoice.Deny);

    public void Resolve(ApprovalChoice choice)
    {
        if (IsResolved)
        {
            return;
        }

        IsResolved = true;
        ResolutionText = choice switch
        {
            ApprovalChoice.AllowAlways => "已允许：本对话不再询问这个工具。",
            ApprovalChoice.AllowOnce => "已允许",
            _ => "已拒绝",
        };
        _resolve(choice);
    }

    public void Cancel()
    {
        if (IsResolved)
        {
            return;
        }

        IsResolved = true;
        ResolutionText = "已取消";
        _resolve(ApprovalChoice.Deny);
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
