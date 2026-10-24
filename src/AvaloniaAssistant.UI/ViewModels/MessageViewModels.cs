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

public abstract partial class MessageViewModel : ObservableObject
{
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;

    public string TimeText => CreatedAt.ToLocalTime().ToString("HH:mm");

    /// <summary>悬停时看的完整时间。跨天翻记录时，光有 HH:mm 分不清是哪天。</summary>
    public string FullTimeText => CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    /// <summary>这一条是不是当天第一条。由对话页在消息变化时统一算，模板只负责显示。</summary>
    [ObservableProperty]
    private bool showDateHeader;

    [ObservableProperty]
    private string dateHeaderText = string.Empty;

    /// <summary>会话内查找当前停在这一条上。只做一层浅底，不改变任何排版。</summary>
    [ObservableProperty]
    private bool isSearchHit;
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

    /// <summary>成功时不占地方显示「完成」，只有还在跑、被停掉或者失败了才需要状态。</summary>
    public bool ShowStatus => IsRunning || IsError || IsAborted;

    /// <summary>开始时间：被按停时用它算出「跑到第几秒被停的」，比留空诚实。</summary>
    private readonly long _startedAt = Environment.TickCount64;

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

    /// <summary>被用户按停、这一轮没跑完。和 IsError 分开：停止是中性事实，不是故障。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    [NotifyPropertyChangedFor(nameof(StatusBrush))]
    [NotifyPropertyChangedFor(nameof(ShowStatus))]
    private bool isAborted;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandHint))]
    private bool isExpanded;

    /// <summary>行尾的箭头只是提示，鼠标悬停时还要有一句话说明点开能看什么。</summary>
    public string ExpandHint => IsExpanded ? "收起参数与结果" : "展开查看参数与结果";

    /// <summary>目标是不是一个网址：网址直接开浏览器，不要拿去资源管理器里找。</summary>
    public bool IsUrl => Target.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || Target.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 只把绝对路径当成可定位的目标。工具经常传相对路径，那要结合工作区才能解析，
    /// 这里猜一个大概率会定位错——宁可不给按钮，也不把用户带到错目录。
    /// </summary>
    public bool IsRootedPath => !IsUrl && Target.Length > 0 && Path.IsPathRooted(Target);

    public bool CanOpenTarget => IsUrl || IsRootedPath;

    /// <summary>命令、查询词这类没法「打开」的目标，至少给一键复制。</summary>
    public bool ShowCopyTarget => Target.Length > 0 && !CanOpenTarget;

    public string OpenTargetHint => IsUrl ? "在浏览器中打开" : "在资源管理器中定位";

    /// <summary>一段灰字没人知道能点，展开区里给一条明确的动作。</summary>
    [RelayCommand]
    private void OpenTarget()
    {
        if (IsUrl)
        {
            Services.Ui.OpenExternal(Target);
        }
        else if (IsRootedPath)
        {
            Services.Ui.RevealInExplorer(Target);
        }
    }

    [RelayCommand]
    private async Task CopyTargetAsync() => await Services.Ui.CopyTextAsync(Target);

    [RelayCommand]
    private async Task CopyResultAsync() => await Services.Ui.CopyTextAsync(Result ?? string.Empty);

    public string StatusText => IsRunning ? "运行中" : IsAborted ? "已中断" : IsError ? "失败" : "完成";

    public IBrush StatusBrush => IsRunning
        ? new SolidColorBrush(Color.Parse("#8A8A82"))
        : IsError
            ? new SolidColorBrush(Color.Parse("#B3261E"))
            : IsAborted
                ? new SolidColorBrush(Color.Parse("#8A8A82"))
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

    /// <summary>
    /// 用户按停时收尾：这一行不能永远写「运行中」。
    /// 耗时算到按下停止那一刻，状态走中性灰——停止不是失败。
    /// </summary>
    public void Abort()
    {
        if (!IsRunning)
        {
            return;
        }

        MarkAborted(Math.Max(0, Environment.TickCount64 - _startedAt));
    }

    /// <summary>从存档恢复「这一轮被停过」时用，耗时就取当时记下的值。</summary>
    public void MarkAborted(long? durationMs)
    {
        DurationMs = durationMs;
        IsRunning = false;
        IsAborted = true;
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
    [NotifyPropertyChangedFor(nameof(HasModel))]
    private string? model;

    public bool HasModel => !string.IsNullOrWhiteSpace(Model);

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
    private readonly Action<ToolApprovalDecision> _resolve;

    public ApprovalMessageViewModel(
        string toolName,
        string label,
        string arguments,
        string reason,
        Action<ToolApprovalDecision> resolve)
    {
        ToolName = toolName;
        Label = label;
        Arguments = arguments;
        Reason = reason;
        EditedArguments = arguments;
        _resolve = resolve;
    }

    public string ToolName { get; }

    public string Label { get; }

    /// <summary>模型给的原始参数。改坏了可以一键还原。</summary>
    public string Arguments { get; }

    public string Reason { get; }

    /// <summary>
    /// 卡片里可以改的参数。SDK 只允许拦截、不允许改写工具参数，
    /// 所以「按修改重试」实际是：拒绝这次调用 + 把改后的参数交给模型重发一次。
    /// 文案和提示都要按这个事实写，不能说成「直接执行」。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEdits))]
    private string editedArguments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditError))]
    private string? editError;

    public bool HasEdits => !string.Equals(
        (EditedArguments ?? string.Empty).Trim(),
        (Arguments ?? string.Empty).Trim(),
        StringComparison.Ordinal);

    public bool HasEditError => !string.IsNullOrWhiteSpace(EditError);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowActions))]
    private bool isResolved;

    [ObservableProperty]
    private string resolutionText = string.Empty;

    public bool ShowActions => !IsResolved;

    /// <summary>已经答过的卡片收成一行，点开才看完整的命令参数。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandHint))]
    private bool isExpanded;

    public string ExpandHint => IsExpanded ? "收起命令详情" : "展开命令详情";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    /// <summary>「本会话都允许 run_command」的按钮文案，带上工具名，别让人误以为是全局开关。</summary>
    public string AlwaysLabel => $"本会话都允许 {ToolName}";

    /// <summary>「本会话都允许这个工具」。怎么记住由宿主决定，卡片只负责发起。</summary>
    public Action? AllowAlwaysRequested { get; set; }

    [RelayCommand]
    private void Allow() => Resolve(ToolApprovalDecision.Allow);

    [RelayCommand]
    private void AllowAlways()
    {
        if (IsResolved)
        {
            return;
        }

        IsResolved = true;
        ResolutionText = "本会话已允许";
        AllowAlwaysRequested?.Invoke();
        _resolve(ToolApprovalDecision.Allow);
    }

    /// <summary>改完参数再跑。校验不过就停在卡片上，别把一段坏 JSON 丢给模型。</summary>
    [RelayCommand]
    private void AllowEdited()
    {
        if (IsResolved)
        {
            return;
        }

        var corrected = (EditedArguments ?? string.Empty).Trim();
        if (corrected.Length == 0)
        {
            EditError = "参数不能为空；不改的话直接点「允许一次」。";
            return;
        }

        // 原始参数是 JSON 的（内置工具都走这条），改完必须还是 JSON：
        // 否则模型收到一段解析不了的文本，只会再错一次。
        if (LooksLikeJson(Arguments))
        {
            try
            {
                using var _ = JsonDocument.Parse(corrected);
            }
            catch (JsonException ex)
            {
                EditError = "改完不是合法 JSON：" + ex.Message;
                return;
            }
        }

        EditError = null;
        Resolve(new ToolApprovalDecision(false, corrected));
    }

    [RelayCommand]
    private void ResetArguments()
    {
        EditedArguments = Arguments;
        EditError = null;
    }

    private static bool LooksLikeJson(string? text)
    {
        var trimmed = (text ?? string.Empty).TrimStart();
        return trimmed.StartsWith('{') || trimmed.StartsWith('[');
    }

    [RelayCommand]
    private void Deny() => Resolve(ToolApprovalDecision.Deny);

    public void Resolve(ToolApprovalDecision decision)
    {
        if (IsResolved)
        {
            return;
        }

        IsResolved = true;
        ResolutionText = decision.Allowed
            ? "已允许"
            : decision.CorrectedArguments is { Length: > 0 }
                ? "已改参数，让模型重试"
                : "已拒绝";
        _resolve(decision);
    }

    public void Cancel()
    {
        if (IsResolved)
        {
            return;
        }

        IsResolved = true;
        ResolutionText = "已取消";
        _resolve(ToolApprovalDecision.Deny);
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
