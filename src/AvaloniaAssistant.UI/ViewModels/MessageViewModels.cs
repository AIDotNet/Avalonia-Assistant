using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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
    public UserMessageViewModel(
        string text,
        IReadOnlyList<ChatAttachment>? attachments = null,
        IReadOnlyList<ChatImage>? images = null)
    {
        Text = text;
        Attachments = attachments ?? [];
        SourceImages = images ?? [];
        Thumbnails = LoadThumbnails(SourceImages);
    }

    public string Text { get; }

    /// <summary>这一轮附带的图片，重试时原样再发一次。</summary>
    public IReadOnlyList<ChatImage> SourceImages { get; }

    /// <summary>气泡里显示的小图。解码时就缩到 120px 宽，几张 4K 截图也不会把内存吃穿。</summary>
    public IReadOnlyList<Bitmap> Thumbnails { get; }

    public bool HasImages => Thumbnails.Count > 0;

    private static IReadOnlyList<Bitmap> LoadThumbnails(IReadOnlyList<ChatImage> images)
    {
        if (images.Count == 0)
        {
            return [];
        }

        var thumbnails = new List<Bitmap>(images.Count);
        foreach (var image in images)
        {
            try
            {
                if (!File.Exists(image.Path))
                {
                    continue;
                }

                using var stream = File.OpenRead(image.Path);
                thumbnails.Add(Bitmap.DecodeToWidth(stream, 120));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 图片被删了或格式坏了，少显示一张，不影响这条消息
            }
        }

        return thumbnails;
    }

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
    [NotifyPropertyChangedFor(nameof(ShowErrorAction))]
    private string? error;

    /// <summary>失败分类的标题，例如「API Key 没通过」。原始报错仍然放在 <see cref="Error"/> 里。</summary>
    [ObservableProperty]
    private string? errorTitle;

    /// <summary>下一步该干什么。</summary>
    [ObservableProperty]
    private string? errorHint;

    /// <summary>按钮文案：去设置 / 重试 / 新建任务。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowErrorAction))]
    private string? errorActionLabel;

    /// <summary>按钮按下去之后走哪条路，由对话上下文决定。</summary>
    public RunErrorKind ErrorActionKind { get; set; }

    public Func<Task>? ErrorActionRequested { get; set; }

    public bool ShowErrorAction => HasError && !string.IsNullOrWhiteSpace(ErrorActionLabel);

    /// <summary>用户主动停止。和 <see cref="Error"/> 分开：停止不是故障，不能画成红框。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStopNote))]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    [NotifyPropertyChangedFor(nameof(ShowContinue))]
    private bool isStopped;

    /// <summary>是不是最后一条回复。「继续」只对最后一条开放：改前面的会把后面全冲掉。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowContinue))]
    private bool isLatest;

    public bool ShowContinue => IsStopped && IsLatest;

    /// <summary>点「继续」时由对话上下文接过去，接着同一条气泡往下写。</summary>
    public Func<Task>? ContinueRequested { get; set; }

    /// <summary>被用户按停之后重新开跑：清掉停止标记，让气泡回到流式状态。</summary>
    public void Resume()
    {
        IsStopped = false;
        IsStreaming = true;
        EnsureTimer();
    }

    /// <summary>复制按钮的回执。点完变「已复制」，一会儿自己复原。</summary>
    [ObservableProperty]
    private string copyLabel = "复制";

    private DispatcherTimer? _copyTimer;

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

    public bool HasStopNote => IsStopped;

    public bool HasTools => Tools.Count > 0;

    public bool HasTokens => InputTokens > 0 || OutputTokens > 0;

    public bool ShowTyping => IsStreaming && !HasText && !HasTools;

    /// <summary>
    /// 只有回复真的结束了才挂操作菜单。失败、被中断、或者整轮只调了工具没出文字时，
    /// 用户更需要「重试」，所以不把没有正文当成不显示的理由。
    /// </summary>
    public bool ShowActions => !IsStreaming && (HasText || HasError || HasTools || IsStopped);


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
    private async Task CopyAsync()
    {
        await Services.Ui.CopyTextAsync(Text);

        // 复制没有回执，用户会以为没点上，再点一次。
        CopyLabel = "已复制";
        _copyTimer?.Stop();
        _copyTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
        _copyTimer.Tick += (_, _) =>
        {
            _copyTimer?.Stop();
            _copyTimer = null;
            CopyLabel = "复制";
        };
        _copyTimer.Start();
    }


    [RelayCommand]
    private Task RetryAsync() => RetryRequested?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private Task ContinueAsync() => ContinueRequested?.Invoke() ?? Task.CompletedTask;

    [RelayCommand]
    private Task RunErrorActionAsync() => ErrorActionRequested?.Invoke() ?? Task.CompletedTask;

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

    /// <summary>「本会话都允许 run_command」的按钮文案，带上工具名，别让人误以为是全局开关。</summary>
    public string AlwaysLabel => $"本会话都允许 {ToolName}";

    /// <summary>「本会话都允许这个工具」。怎么记住由宿主决定，卡片只负责发起。</summary>
    public Action? AllowAlwaysRequested { get; set; }

    [RelayCommand]
    private void Allow() => Resolve(true);

    [RelayCommand]
    private void AllowAlways()
    {
        if (IsResolved)
        {
            return;
        }

        IsResolved = true;
        ResolutionText = $"本会话都允许 {ToolName}";
        AllowAlwaysRequested?.Invoke();
        _resolve(true);
    }

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
