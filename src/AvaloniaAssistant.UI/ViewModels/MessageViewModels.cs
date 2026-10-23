using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaAssistant.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ArcIcons = TKCode.Arc.ArcIcons;

namespace AvaloniaAssistant.UI.ViewModels;

public abstract class MessageViewModel : ObservableObject
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string TimeText => CreatedAt.ToLocalTime().ToString("HH:mm");
}

public sealed partial class UserMessageViewModel : MessageViewModel
{
    public UserMessageViewModel(string text, IReadOnlyList<ChatAttachment>? attachments = null)
    {
        Text = text;
        Attachments = attachments ?? [];
    }

    public string Text { get; }

    /// <summary>这一轮 @ 进来的资料。气泡里只显示名字，正文不进界面。</summary>
    public IReadOnlyList<ChatAttachment> Attachments { get; }

    public bool HasAttachments => Attachments.Count > 0;

    public string AttachmentText => string.Join("、", Attachments.Select(item => item.Name));

    public string AttachmentLabel => Attachments.Count == 1
        ? $"引用了 {AttachmentText}"
        : $"引用了 {Attachments.Count} 份：{AttachmentText}";
}

public sealed partial class ToolCallViewModel : ObservableObject
{
    /// <summary>从参数里挑「这次动的是哪个东西」时优先看的字段，越靠前越具体。</summary>
    private static readonly string[] TargetKeys =
    [
        "command", "path", "file_path", "filePath", "pattern", "url", "query", "name", "directory",
    ];

    public ToolCallViewModel(string id, string name, string label, string arguments)
    {
        Id = id;
        Name = name;
        Label = label;
        Arguments = arguments;
        Target = ExtractTarget(arguments);
    }

    public string Id { get; }

    public string Name { get; }

    public string Label { get; }

    /// <summary>参数里最能说明「在操作什么」的那一段：命令、路径或查询词。</summary>
    public string Target { get; }

    /// <summary>对话流里那一行字，形如「执行命令：dotnet build」。</summary>
    public string Summary => Target.Length == 0 ? Label : $"{Label}：{Target}";

    public Geometry Icon => Name switch
    {
        "run_command" => ArcIcons.Command,
        "read_file" => ArcIcons.FileText,
        "write_file" or "edit" => ArcIcons.Pencil,
        "search_files" or "grep" or "find" => ArcIcons.Search,
        "list_directory" or "ls" => ArcIcons.Folder,
        "fetch_url" or "open_url" => ArcIcons.Globe,
        "current_time" => ArcIcons.Clock,
        _ => ArcIcons.Zap,
    };

    /// <summary>成功时不占地方显示「完成」，只有还在跑或者失败了才需要状态。</summary>
    public bool ShowStatus => IsRunning || IsError;

    [ObservableProperty]
    private string arguments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    private string? result;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    private bool isError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private long? durationMs;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
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

    private static string ExtractTarget(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in TargetKeys)
                {
                    if (document.RootElement.TryGetProperty(key, out var value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        return Shorten(value.GetString());
                    }
                }
            }
        }
        catch (JsonException)
        {
            // MCP 或自定义工具的参数可能不是 JSON，退回整串的第一行
        }

        return Shorten(arguments);
    }

    private static string Shorten(string? value)
    {
        var text = (value ?? string.Empty).Trim().ReplaceLineEndings(" ");
        if (text.Length == 0)
        {
            return string.Empty;
        }

        return text.Length <= 72 ? text : text[..72] + "…";
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
