using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed partial class ComposerViewModel : ObservableObject, IDisposable
{
    /// <summary>行首或空格后的 @，一路到行尾；用户在末尾打 @ 才会触发，不会误伤邮箱地址。</summary>
    private static readonly Regex MentionPattern = new(@"(?:^|\s)@([^\s@]*)$", RegexOptions.Compiled);

    private readonly IAssistantRuntime _runtime;
    private readonly ISettingsProvider _settings;
    private readonly IFilePicker _picker;
    private readonly Func<Task> _openSettings;
    private readonly IVoiceInput _voice;
    private readonly EventHandler _settingsChanged;

    private readonly WorkspaceFileSearch _fileSearch;
    private string? _indexedWorkspace;

    /// <summary>自己在改 Text（补全 @、清空）时不要再弹一次浮层。</summary>
    private bool _suppressMention;

    /// <summary>@ 候选的加载序号：并发输入时只认最后一次。</summary>
    private int _mentionVersion;

    private bool _indexed;

    public ComposerViewModel(MainWindowViewModel host)
    {
        Host = host;
        _runtime = host.Runtime;
        _settings = host.SettingsProvider;
        _picker = host.Picker;
        _voice = host.Voice;
        _openSettings = host.OpenSettingsAsync;
        _fileSearch = host.FileSearch;
        _settingsChanged = (_, _) => Refresh();
        RefreshModels();
        _settings.Changed += _settingsChanged;
    }

    /// <summary>工作区浮层要读整份设置和空间列表，都挂在主窗口 VM 上。</summary>
    public MainWindowViewModel Host { get; }

    public event EventHandler<PromptRequest>? SendRequested;

    /// <summary>正在生成时按下发送：不丢内容，交给对话页排到本轮结束后自动发出。</summary>
    public event EventHandler<PromptRequest>? QueueRequested;

    /// <summary>点了排队提示条上的「取消」。</summary>
    public event EventHandler? QueueCancelled;

    public event EventHandler? FocusRequested;

    /// <summary>
    /// 发送前的二次确认（目前只用于「有引用找不到」）。由 ComposerView 接上弹窗；
    /// 没接的时候按「仍然发送」处理，不拦用户。
    /// </summary>
    public Func<string, string, string, string, Task<bool>>? ConfirmAsync { get; set; }

    public event EventHandler? StopRequested;

    /// <summary>权限档位变化。对话页据此插一条提示，让用户知道确认行为变了。</summary>
    public event EventHandler<PermissionMode>? PermissionChanged;

    /// <summary>设置写盘失败。界面要能说出来，否则档位会莫名其妙退回去。</summary>
    public event EventHandler<string>? SettingsWriteFailed;

    public ObservableCollection<ModelOption> Models { get; } = [];

    /// <summary>这一轮 @ 进来的文件，发送时变成 <see cref="PromptRequest.Attachments"/>。</summary>
    public ObservableCollection<MentionItemViewModel> References { get; } = [];

    /// <summary>@ 浮层里的候选，最多 8 条。</summary>
    public ObservableCollection<MentionItemViewModel> MentionItems { get; } = [];

    /// <summary>待发送的图片（粘贴进来的）。</summary>
    public ObservableCollection<PendingImageViewModel> Images { get; } = [];

    public bool HasImages => Images.Count > 0;

    /// <summary>
    /// 视觉模型的常用输入长边上限。超过这个尺寸的图不会多看到细节，
    /// 只会多花 token——落盘前先缩，原图尺寸另存在 ChatImage 里。
    /// </summary>
    private const int MaxImageEdge = 1568;

    /// <summary>一条消息里贴太多图时提醒一句：请求会明显变贵，而且是倍数级。</summary>
    public bool ShowManyImagesHint => Images.Count > 3;

    public string ManyImagesHint => $"已贴 {Images.Count} 张图，这一轮请求会明显变贵。";

    public bool HasReferences => References.Count > 0;

    public bool HasMentionItems => MentionItems.Count > 0;

    [ObservableProperty]
    private bool showMention;

    [ObservableProperty]
    private int mentionIndex;

    [ObservableProperty]
    private string mentionQuery = string.Empty;

    [ObservableProperty]
    private string mentionEmptyText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string? text;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSend))]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    [NotifyPropertyChangedFor(nameof(InputHint))]
    private bool isBusy;

    public bool ShowSend => !IsBusy;

    /// <summary>这一轮排队等着的消息，由对话页写好；为空表示没有排队。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueued))]
    private string? queuedHint;

    /// <summary>排队几条。超过一条时取消按钮要写明「全部取消」，别让人以为只丢最后一条。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(QueuedCancelLabel))]
    private int queuedCount;

    public bool HasQueued => !string.IsNullOrWhiteSpace(QueuedHint);

    public string QueuedCancelLabel => QueuedCount > 1 ? "全部取消" : "取消";

    /// <summary>
    /// 生成期间按 Enter 不是"没反应"，是排队。把这件事写在原来那行提示里，
    /// 用户不用先按一次才发现行为变了。
    /// </summary>
    public string InputHint => IsTranscribing
        ? "正在转写这段录音…按 Enter 会在转完后发送"
        : IsSending
            ? "正在读取引用，按 Enter 会在读完后再发"
            : IsBusy
                ? "Enter 排队，本轮结束后自动发送"
                : "Enter 发送 · Shift+Enter 换行 · @ 引用文件";

    /// <summary>正在读引用文件的正文，读完才真正发出。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(InputHint))]
    private bool isSending;

    /// <summary>录音已经停了，正在等转写结果。这一段以前在界面上完全没有痕迹。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputHint))]
    [NotifyPropertyChangedFor(nameof(CanUseVoice))]
    [NotifyPropertyChangedFor(nameof(MicTooltip))]
    // CanSend 会拦转写中；不通知的话按钮仍停在上一次判定上，点下去照样把旧文字发出去
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool isTranscribing;

    /// <summary>读引用的过程中用户按了 Enter：读完就把输入框里剩下的内容发出去。</summary>
    private bool _sendAfterReading;

    /// <summary>转写期间按过 Enter：转完自动发。和读引用那条排队是两个时机。</summary>
    private bool _sendAfterTranscribing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MicTooltip))]
    [NotifyPropertyChangedFor(nameof(MicBrush))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool isRecording;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVoiceError))]
    private string? voiceError;

    /// <summary>没配模型服务时的就地提示。不跳页、不清空输入。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSetupHint))]
    private string? setupHint;

    public bool HasSetupHint => !string.IsNullOrWhiteSpace(SetupHint);

    /// <summary>正在读引用 / 正在录音时的一行状态。之前这两种等待在界面上完全没有痕迹。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActivity))]
    private string? activityText;

    public bool HasActivity => !string.IsNullOrWhiteSpace(ActivityText);

    /// <summary>本次对话里被「都允许」放行的工具数。挂在权限 pill 上，别让人忘了自己关过确认。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSessionAllowedTools))]
    [NotifyPropertyChangedFor(nameof(SessionAllowedBadge))]
    [NotifyPropertyChangedFor(nameof(PermissionTooltip))]
    private int sessionAllowedCount;

    public bool HasSessionAllowedTools => SessionAllowedCount > 0;

    public string SessionAllowedBadge => $"已放行 {SessionAllowedCount}";

    /// <summary>
    /// 生成中切了权限：这一轮仍按开始时的档位执行，pill 上要标明「下一轮生效」。
    /// 否则界面写着只读，正在跑的这一轮还在改文件，用户会以为权限没生效。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PermissionTooltip))]
    private bool permissionPendingNextRun;

    /// <summary>权限浮层里的「恢复逐次确认」，由对话页负责真正清空放行记录。</summary>
    public event EventHandler? ResetSessionAllowedRequested;

    [RelayCommand]
    private void ResetSessionAllowed() => ResetSessionAllowedRequested?.Invoke(this, EventArgs.Empty);

    public bool HasVoiceError => !string.IsNullOrWhiteSpace(VoiceError);

    /// <summary>贴图存盘失败时的提示。以前吞掉异常，用户只看到「粘了没反应」。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasImageError))]
    private string? imageError;

    public bool HasImageError => !string.IsNullOrWhiteSpace(ImageError);

    private DispatcherTimer? _imageErrorTimer;

    /// <summary>图片没能落盘：写在输入框下面，几秒后自己消失，不长期占位置。</summary>
    public void NotifyImageError(string message)
    {
        ImageError = message;

        _imageErrorTimer?.Stop();
        _imageErrorTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _imageErrorTimer.Tick += (_, _) =>
        {
            _imageErrorTimer?.Stop();
            _imageErrorTimer = null;
            ImageError = null;
        };
        _imageErrorTimer.Start();
    }

    public bool CanUseVoice => !IsBusy && !IsTranscribing;

    public string MicTooltip => IsTranscribing
        ? "正在转写，稍等"
        : IsBusy
            ? "正在生成，本轮结束后可用"
            : IsRecording ? "停止并转写" : "语音输入";

    public IBrush MicBrush => IsRecording
        ? new SolidColorBrush(Color.Parse("#B3261E"))
        : new SolidColorBrush(Color.Parse("#6F6F69"));

    public bool ShowStop => IsBusy;

    public string Placeholder => "描述你要做的事，越具体越好";

    /// <summary>输入框下方那颗胶囊上的字。起了名字就显示名字，否则显示模型名。</summary>
    public string ModelLabel => _settings.Current.ActiveModel?.DisplayName ?? _settings.Current.Model;

    /// <summary>模型浮层用它给当前项打勾——比的是配置 id，不是模型名：两套配置可以指向同一个模型。</summary>
    public string CurrentProfileId => _settings.Current.ActiveModelId ?? string.Empty;

    /// <summary>胶囊的悬停说明。三行说清「现在到底在用什么」：名字、模型与协议、端点。</summary>
    public string ModelTooltip
    {
        get
        {
            if (_settings.Current.ActiveModel is not { } active)
            {
                return "还没有配置模型：去设置里添加一套";
            }

            return $"当前模型：{active.DisplayName}\n{active.Model} · {ProtocolCatalog.For(active.EffectiveProtocol).Name}\n{active.BaseUrl}\n点击切换";
        }
    }

    public string PermissionLabel => PermissionModeText.Describe(_settings.Current.Permission);

    /// <summary>当前档位，权限浮层据此画选中态。</summary>
    public PermissionMode Permission => _settings.Current.Permission;

    /// <summary>权限说明，展示在权限浮层顶部。</summary>
    public string PermissionDetail => PermissionModeText.Detail(_settings.Current.Permission);

    public string PermissionTooltip
    {
        get
        {
            var scope = PermissionPendingNextRun ? "，下一轮生效" : string.Empty;
            return HasSessionAllowedTools
                ? $"工具权限：{PermissionLabel}{scope}（本次已放行 {SessionAllowedCount} 个工具）"
                : $"工具权限：{PermissionLabel}{scope}";
        }
    }

    /// <summary>是否处于完全访问。开关直接绑定这个属性，读写都走 <see cref="SetPermissionAsync"/>。</summary>
    public bool IsFullAccess
    {
        get => _settings.Current.Permission == PermissionMode.FullAccess;
        set
        {
            if (value == IsFullAccess)
            {
                return;
            }

            _ = SetPermissionAsync(value ? PermissionMode.FullAccess : PermissionMode.Workspace);
        }
    }

    public bool IsReadOnlyMode => _settings.Current.Permission == PermissionMode.ReadOnly;

    /// <summary>完全访问用警示色，平时跟普通 pill 一样低调。</summary>
    public IBrush PermissionForeground => _settings.Current.Permission == PermissionMode.FullAccess
        ? new SolidColorBrush(Color.Parse("#8A4B00"))
        : new SolidColorBrush(Color.Parse("#6F6F69"));

    /// <summary>完全访问时在输入框下方给一条常驻提醒。</summary>
    public bool ShowFullAccessNotice => _settings.Current.Permission == PermissionMode.FullAccess;

    /// <summary>
    /// 这条任务已经开口之后钉住的目录。null 表示还没发过话，跟着当前选择走。
    /// 以前胶囊一律读全局设置：已开始的会话里换目录，胶囊变了、任务没变，
    /// 页头副标题又显示旧目录——同一屏两个答案，而模型读写的是后者。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkspacePinned))]
    [NotifyPropertyChangedFor(nameof(WorkspaceLabel))]
    [NotifyPropertyChangedFor(nameof(WorkspacePath))]
    [NotifyPropertyChangedFor(nameof(WorkspacePathHint))]
    private string? pinnedWorkspacePath;

    public bool IsWorkspacePinned => !string.IsNullOrWhiteSpace(PinnedWorkspacePath);

    public string WorkspaceLabel
    {
        get
        {
            var name = WorkspaceNaming.ShortName(WorkspacePath);
            return name.Length > 0 ? name : "普通任务";
        }
    }

    /// <summary>这条任务真正会读写的目录：钉住了用钉住的，否则跟着设置。</summary>
    public string WorkspacePath => IsWorkspacePinned ? PinnedWorkspacePath! : _settings.Current.WorkspacePath;

    /// <summary>
    /// 胶囊的悬停提示。普通任务也有落点（用户目录下的 .avaloniaassistant），
    /// 得说出来——不然让它写的报告到底存哪了，只能靠猜。
    /// 钉住的会话要额外说清「换工作区不动这条任务」，否则用户会以为这一下改了当前任务。
    /// 别写成「只对下一个任务生效」：侧栏那个「新建任务」会先清掉工作区，承诺不成立。
    /// </summary>
    public string WorkspacePathHint => IsWorkspacePinned
        ? $"这条任务固定在 {WorkspacePath}；换工作区不会改动它"
        : string.IsNullOrWhiteSpace(WorkspacePath)
            ? $"文件落在 {Ui.ShortenPath(PlainWorkspace.Root)}"
            : WorkspacePath;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Current.ApiKey);

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (IsRecording)
        {
            CancelRecording();
        }

        var value = Text?.Trim() ?? string.Empty;
        if (value.Length == 0 && Images.Count == 0 && References.Count == 0)
        {
            return;
        }

        // 读引用要几秒，这期间用户接着敲的字不能被 `Text = ""` 一起抹掉。
        // 先留一份原文，读完只清掉真正发出去的那一段。
        var originalText = Text ?? string.Empty;

        if (!IsConfigured)
        {
            // 以前这里直接切到设置页：页面一换，ChatViewModel 连同刚打的两百字一起被 Dispose。
            // 现在留在原地提示，文字不动，用户自己决定什么时候去配。
            SetupHint = "还没配模型服务：先在设置里填 API Key，这段文字会留在这里。";
            RequestFocus();
            return;
        }

        SetupHint = null;

        if (value.Length == 0)
        {
            value = Images.Count > 0 ? "看下这张图。" : "看下这些文件。";
        }

        // 先记下这一下是"发送"还是"排队"：读取引用的过程中 IsSending 会变，
        // 但用户按 Enter 那一刻的意图不能跟着变。
        var queue = IsBusy;

        // 引用是路径不是快照：文件可能在引用之后被挪走。发之前对一次盘，
        // 别等模型回一句「没有这个文件」——那时候用户已经以为资料给了。
        foreach (var reference in References)
        {
            reference.RefreshExists();
        }

        var missing = References.Where(item => item.IsMissing).ToList();
        if (missing.Count > 0 && ConfirmAsync is not null)
        {
            var names = string.Join("、", missing.Take(3).Select(item => item.Name));
            if (missing.Count > 3)
            {
                names += $" 等 {missing.Count} 个";
            }

            var remove = await ConfirmAsync(
                $"{missing.Count} 个引用找不到",
                $"{names} 已经被移动或删除。移除这些引用之后再发？也可以仍然发送，模型会收到「文件不存在」的说明。",
                "移除后发送",
                "仍然发送");

            if (remove)
            {
                foreach (var item in missing)
                {
                    References.Remove(item);
                }

                OnPropertyChanged(nameof(HasReferences));
            }
        }

        // 引用的是工作区文件时，正文要现场读：文件可能刚被改过，索引里的旧内容不作数。
        IsSending = true;
        try
        {
            var attachments = new List<ChatAttachment>(References.Count);
            foreach (var reference in References)
            {
                attachments.Add(await BuildAttachmentAsync(reference));
            }

            var images = Images.Select(item => item.Image).ToList();

            KeepTextTypedWhileReading(originalText);
            References.Clear();
            Images.Clear();
            OnPropertyChanged(nameof(HasReferences));
            OnPropertyChanged(nameof(HasImages));
            CloseMention();

            var request = new PromptRequest(value, attachments, images);
            if (queue)
            {
                QueueRequested?.Invoke(this, request);
            }
            else
            {
                SendRequested?.Invoke(this, request);
            }

            RequestFocus();
        }
        finally
        {
            IsSending = false;
        }

        // 读引用时按过 Enter：刚才那句话已经发出去了，这里把后来补的内容接着发。
        // 不这样做，那一下 Enter 就是纯静默失败。
        if (_sendAfterReading)
        {
            _sendAfterReading = false;
            UpdateActivity();
            if (SendCommand.CanExecute(null))
            {
                await SendAsync();
            }
        }
    }

    /// <summary>
    /// 只清掉已经发出去的那段文字。用户在读引用期间补了新内容时保留下来，
    /// 否则那几个字会跟着 Text 一起被清空——他明明看见自己打上去了。
    /// </summary>
    private void KeepTextTypedWhileReading(string sent)
    {
        var current = Text ?? string.Empty;

        if (string.Equals(current, sent, StringComparison.Ordinal))
        {
            Text = string.Empty;
            return;
        }

        // 原文还在前面：去掉它，只留新敲的部分；已经被改得面目全非时原样保留
        Text = current.StartsWith(sent, StringComparison.Ordinal)
            ? current[sent.Length..].TrimStart()
            : current;
    }

    /// <summary>
    /// 转写期间按了 Enter。这段时间输入框是锁着的（CanSend 拦掉了），
    /// 不记下来的话这一下就是纯吞键；记下来，转完把结果发出去。
    /// </summary>
    public void SendAfterTranscription()
    {
        if (!IsTranscribing || _sendAfterTranscribing)
        {
            return;
        }

        _sendAfterTranscribing = true;
        UpdateActivity();
    }

    /// <summary>
    /// 读引用期间按了 Enter。不抢那一下按键，记下来等读完再发。
    /// 和生成期间的「排队」是同一个承诺：按下去一定有回应。
    /// </summary>
    public void SendAfterReading()
    {
        if (!IsSending || _sendAfterReading)
        {
            return;
        }

        _sendAfterReading = true;
        UpdateActivity();
    }

    /// <summary>把一个引用变成模型能读的附件：发送时才读盘，文件刚改过也能读到新内容。</summary>
    private static async Task<ChatAttachment> BuildAttachmentAsync(MentionItemViewModel item)
    {
        var attachment = new ChatAttachment
        {
            Id = item.Id,
            Name = item.Name,
            Path = item.Path,
            FullPath = item.FullPath,
            Source = "workspace",
            Kind = item.Kind,
            Size = item.Size,
        };

        if (string.IsNullOrWhiteSpace(item.FullPath) || !File.Exists(item.FullPath))
        {
            attachment.Error = "文件不存在，可能已经被移动或删除";
            return attachment;
        }

        var (text, lines, truncated) = await Task.Run(() => FileContentReader.Read(item.FullPath!));
        attachment.Text = text;
        attachment.LineCount = lines;
        attachment.Truncated = truncated;
        return attachment;
    }

    /// <summary>
    /// 生成期间也能发（会变成排队），所以这里不再拦 IsBusy：
    /// 拦掉的结果是 Enter 被吞、界面一声不响，比排队糟得多。
    /// </summary>
    private bool CanSend() => !IsSending && !IsRecording && !IsTranscribing
        && (!string.IsNullOrWhiteSpace(Text) || Images.Count > 0 || References.Count > 0);

    partial void OnTextChanged(string? value)
    {
        if (_suppressMention)
        {
            return;
        }

        UpdateMention(value);
    }

    /// <summary>只在「正在打一个 @ 词」时开浮层：行尾是 <c>@abc</c> 才认，邮箱和代码里的 @ 不会误触。</summary>
    private void UpdateMention(string? text)
    {
        var match = MentionPattern.Match(text ?? string.Empty);
        if (!match.Success)
        {
            CloseMention();
            return;
        }

        MentionQuery = match.Groups[1].Value;

        // 先把浮层开出来再去找：第一次按 @ 要扫一遍工作区，几百毫秒里什么都不显示，
        // 人只会以为这个键没反应。
        ShowMention = true;
        MentionIndex = -1;
        MentionEmptyText = "正在查找…";
        MentionItems.Clear();
        OnPropertyChanged(nameof(HasMentionItems));

        _ = RefreshMentionAsync();
    }

    private async Task RefreshMentionAsync()
    {
        var version = ++_mentionVersion;

        await EnsureWorkspaceIndexAsync();

        // 等待扫盘的这段时间用户又敲了字，这一次结果已经过期
        if (version != _mentionVersion)
        {
            return;
        }

        var query = MentionQuery;
        var workspace = SearchRoot;

        // 扫盘丢线程池，别让输入卡住
        var files = await Task.Run(() => _fileSearch.Search(workspace, query, 12));
        if (version != _mentionVersion)
        {
            return;
        }

        var candidates = new List<(MentionItemViewModel Item, int Score)>();
        var rank = 0;

        // 空查询时按「最近改过」的次序排，打分的意义只在有查询词时
        foreach (var file in files)
        {
            var score = query.Length == 0
                ? rank++
                : WorkspaceFileSearch.Score(file.RelativePath, query);
            candidates.Add((MentionItemViewModel.FromWorkspace(file), score));
        }

        var items = candidates
            .OrderBy(candidate => candidate.Score)
            .ThenBy(candidate => candidate.Item.Name, StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .Select(candidate => candidate.Item)
            .ToList();

        MentionItems.Clear();
        foreach (var item in items)
        {
            MentionItems.Add(item);
        }

        MentionIndex = items.Count > 0 ? 0 : -1;
        ApplyMentionSelection();
        MentionEmptyText = BuildMentionEmptyText(workspace, query);
        ShowMention = true;
        OnPropertyChanged(nameof(HasMentionItems));
    }

    /// <summary>
    /// @ 和引用要找的目录。没选工作区的任务也有地方读写：用户目录下那格 .avaloniaassistant，
    /// 所以这里用「真正会用的目录」，而不是设置里那个可能为空的字符串。
    /// </summary>
    private string SearchRoot => PlainWorkspace.Resolve(_settings.Current.WorkspacePath);

    private string BuildMentionEmptyText(string workspace, string query)
    {
        // 没选工作区时这里是普通任务目录：说清楚找的是哪儿，别让人以为 @ 坏了
        var scope = string.IsNullOrWhiteSpace(_settings.Current.WorkspacePath)
            ? $"普通任务目录（{Ui.ShortenPath(workspace)}）"
            : "工作区";

        return query.Length > 0
            ? $"{scope}里没有带「{query}」的文件。"
            : $"{scope}里还没有可引用的文件。";
    }

    private async Task EnsureWorkspaceIndexAsync()
    {
        var workspace = SearchRoot;
        if (_indexed && string.Equals(_indexedWorkspace, workspace, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // 预热一遍文件索引，别让用户第一次按 @ 时等扫盘
        await Task.Run(() => _fileSearch.List(workspace));

        _indexedWorkspace = workspace;
        _indexed = true;
    }

    /// <summary>
    /// 按名字引用一个工作区文件，不改输入框文字。给自动化验收用，
    /// 将来「从文件树拖进对话」也走这里。
    /// </summary>
    public async Task<bool> ReferenceByNameAsync(string name)
    {
        await EnsureWorkspaceIndexAsync();

        var workspace = SearchRoot;
        var file = _fileSearch
            .Search(workspace, name, 8)
            .FirstOrDefault(entry => entry.RelativePath.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (file is null)
        {
            return false;
        }

        AddReference(MentionItemViewModel.FromWorkspace(file));
        return true;
    }

    /// <summary>
    /// 把一个本地文件变成待发送的引用。拖放进来的文件和纸夹选、@ 引用走同一条链路：
    /// 发送时才读正文。返回 false 表示文件不在或已经引用过了。
    /// </summary>
    public bool AddFileReference(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
        {
            return false;
        }

        var added = AddReference(MentionItemViewModel.FromFile(fullPath));
        RequestFocus();
        return added;
    }

    /// <summary>一次拖放加进来的东西：文件几次、图片几张、文件夹几个、重复几个、失败几个。</summary>
    public readonly record struct DropAcceptance(int Files, int Images, int Folders, int Duplicates, int Failed)
    {
        public bool Added => Files + Images > 0;
    }

    /// <summary>
    /// 接住一次拖放。图片走贴图那条路（落盘 + 缩略图，发送时当图片交给模型），
    /// 其它文件走引用那条路（发送时才读正文）。两条路都和纸夹、@、Ctrl+V 共用同一份实现。
    /// </summary>
    public async Task<DropAcceptance> AcceptDropAsync(IReadOnlyList<string> paths, Bitmap? bitmap)
    {
        var files = 0;
        var images = 0;
        var folders = 0;
        var duplicates = 0;
        var failed = 0;

        if (bitmap is not null)
        {
            if (AddImage(bitmap))
            {
                images++;
            }
            else
            {
                bitmap.Dispose();
                failed++;
            }
        }

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                folders++;
                continue;
            }

            if (!File.Exists(path))
            {
                failed++;
                continue;
            }

            // 图片必须当图片：当引用发出去的话，发送时会把二进制当文本读，模型拿到的是乱码
            if (ComposerDropPayload.IsImagePath(path))
            {
                var decoded = await DecodeImageAsync(path);
                if (decoded is not null)
                {
                    if (AddImage(decoded))
                    {
                        images++;
                        continue;
                    }

                    decoded.Dispose();
                }
            }

            if (AddFileReference(path))
            {
                files++;
            }
            else
            {
                // 存在性上面已经查过，这里返回 false 基本就是「已经在引用里了」
                duplicates++;
            }
        }

        if (files + images > 0)
        {
            RequestFocus();
        }

        if (folders > 0)
        {
            NotifyDropNotice("文件夹不能直接拖进来：把工作区切到那个目录，再用 @ 引用里面的文件。");
        }
        else if (failed > 0 && files + images == 0)
        {
            NotifyDropNotice("这几个文件没能加进来：可能已经被移走，或者没有读取权限。");
        }
        else if (duplicates > 0 && files + images == 0)
        {
            NotifyDropNotice("这个文件已经在引用里了。");
        }

        return new DropAcceptance(files, images, folders, duplicates, failed);
    }

    /// <summary>
    /// 把磁盘上的图读成位图。解码很吃 CPU（4K 截图几十毫秒起），扔到后台线程，
    /// 免得拖一张大图进来界面顿一下。解不出来返回 null，调用方退回成引用。
    /// </summary>
    private static async Task<Bitmap?> DecodeImageAsync(string path)
    {
        try
        {
            return await Task.Run(() => new Bitmap(path));
        }
        catch
        {
            // 损坏、格式不支持、权限不足：都当解不出来
            return null;
        }
    }

    /// <summary>拖放相关的提示（文件夹被拒、文件加不进来）。和贴图错误分开，措辞才不会串。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDropNotice))]
    private string? dropNotice;

    public bool HasDropNotice => !string.IsNullOrWhiteSpace(DropNotice);

    private DispatcherTimer? _dropNoticeTimer;

    /// <summary>拖放提示只占一会儿，几秒后自己消失，不长期压在输入框下面。</summary>
    public void NotifyDropNotice(string message)
    {
        DropNotice = message;

        _dropNoticeTimer?.Stop();
        _dropNoticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6) };
        _dropNoticeTimer.Tick += (_, _) =>
        {
            _dropNoticeTimer?.Stop();
            _dropNoticeTimer = null;
            DropNotice = null;
        };
        _dropNoticeTimer.Start();
    }

    private bool AddReference(MentionItemViewModel item)
    {
        if (References.Any(reference => reference.Id == item.Id))
        {
            return false;
        }

        // @ 的候选来自扫盘索引，文件可能在这之间被删；统一在这里对一次
        item.RefreshExists();
        References.Add(item);
        OnPropertyChanged(nameof(HasReferences));
        SendCommand.NotifyCanExecuteChanged();
        return true;
    }

    [RelayCommand]
    private void AcceptMention(MentionItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var text = Text ?? string.Empty;
        var match = MentionPattern.Match(text);
        var replacement = "@" + item.Name + " ";

        if (match.Success)
        {
            // 保留 @ 前面那个空格，只替换掉「@query」
            var start = match.Index + match.Value.Length - match.Groups[1].Length - 1;
            text = text[..start] + replacement;
        }
        else
        {
            text += replacement;
        }

        _suppressMention = true;
        Text = text;
        _suppressMention = false;

        AddReference(item);
        CloseMention();
    }

    [RelayCommand]
    private void RemoveReference(MentionItemViewModel? item)
    {
        if (item is null || !References.Remove(item))
        {
            return;
        }

        OnPropertyChanged(nameof(HasReferences));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>工具条上的「@」：在光标末尾补一个 @ 并直接把浮层打开，不用先打字再想词。</summary>
    [RelayCommand]
    private void OpenMention()
    {
        var text = Text ?? string.Empty;
        if (text.Length > 0 && !char.IsWhiteSpace(text[^1]))
        {
            text += " ";
        }

        _suppressMention = true;
        Text = text + "@";
        _suppressMention = false;

        UpdateMention(Text);
        RequestFocus();
    }

    /// <summary>
    /// Ctrl+V 粘到一张图：落盘成 PNG，挂到待发送列表。
    /// 写盘失败（权限、磁盘满）就当没粘上，不弹框打断输入。
    /// </summary>
    public bool AddImage(Bitmap bitmap)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Images);

            var id = Guid.NewGuid().ToString("n")[..12];
            var path = Path.Combine(AppPaths.Images, id + ".png");

            var sourceWidth = bitmap.PixelSize.Width;
            var sourceHeight = bitmap.PixelSize.Height;
            var scale = Math.Min(1d, MaxImageEdge / (double)Math.Max(sourceWidth, sourceHeight));

            Bitmap? scaled = null;
            var stored = bitmap;
            var storedWidth = sourceWidth;
            var storedHeight = sourceHeight;
            if (scale < 1)
            {
                storedWidth = Math.Max(1, (int)Math.Round(sourceWidth * scale));
                storedHeight = Math.Max(1, (int)Math.Round(sourceHeight * scale));
                scaled = bitmap.CreateScaledBitmap(
                    new PixelSize(storedWidth, storedHeight),
                    BitmapInterpolationMode.HighQuality);
                stored = scaled;
            }

            try
            {
                stored.Save(path);
            }
            finally
            {
                // 只放自己创建的副本；调用方传进来的位图还要拿去当缩略图
                scaled?.Dispose();
            }

            var image = new ChatImage
            {
                Id = id,
                Path = path,
                MimeType = "image/png",
                Width = storedWidth,
                Height = storedHeight,
                OriginalWidth = sourceWidth,
                OriginalHeight = sourceHeight,
                Compressed = scale < 1,
                Size = new FileInfo(path).Length,
            };

            Images.Add(new PendingImageViewModel(image, bitmap, RemoveImage));
            OnPropertyChanged(nameof(HasImages));
            OnPropertyChanged(nameof(ShowManyImagesHint));
            OnPropertyChanged(nameof(ManyImagesHint));
            SendCommand.NotifyCanExecuteChanged();
            ImageError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void RemoveImage(PendingImageViewModel item)
    {
        if (Images.Remove(item))
        {
            OnPropertyChanged(nameof(HasImages));
            OnPropertyChanged(nameof(ShowManyImagesHint));
            OnPropertyChanged(nameof(ManyImagesHint));
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CloseMention()    {
        _mentionVersion++;
        ShowMention = false;
        MentionIndex = -1;
        MentionItems.Clear();
        OnPropertyChanged(nameof(HasMentionItems));
    }

    partial void OnMentionIndexChanged(int value) => ApplyMentionSelection();

    /// <summary>
    /// 选中态落到具体候选上。不能只靠 OnMentionIndexChanged——
    /// 从「没有选中」变成 0 是变化，但重开浮层时初值本来就是 0，不会再触发一次。
    /// </summary>
    private void ApplyMentionSelection()
    {
        for (var index = 0; index < MentionItems.Count; index++)
        {
            MentionItems[index].IsSelected = index == MentionIndex;
        }
    }

    /// <summary>键盘上下移动候选，到边界绕回去，别让手停下来。</summary>
    public void MoveMention(int delta)
    {
        if (!ShowMention || MentionItems.Count == 0)
        {
            return;
        }

        var next = (MentionIndex + delta) % MentionItems.Count;
        if (next < 0)
        {
            next += MentionItems.Count;
        }

        MentionIndex = next;
    }

    public void AcceptCurrentMention()
    {
        if (ShowMention && MentionIndex >= 0 && MentionIndex < MentionItems.Count)
        {
            AcceptMention(MentionItems[MentionIndex]);
        }
    }

    [RelayCommand]
    private void Stop() => StopRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>取消排队。内容已经离开输入框了，所以这里只负责收提示，真正的丢弃在对话页。</summary>
    [RelayCommand]
    private void CancelQueue()
    {
        QueuedHint = null;
        QueueCancelled?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>提示条上的「去设置」。用户主动点的，跳走之后草稿由宿主接住。</summary>
    [RelayCommand]
    private async Task OpenSettingsAsync() => await _openSettings();


    public void CancelRecording()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        _voice.Cancel();
    }

    [RelayCommand]
    private async Task ToggleVoiceAsync()
    {
        if (IsBusy)
        {
            return;
        }

        if (!IsConfigured)
        {
            // 和发送按钮一样就地说明：点麦克风却整页跳到设置，比什么都不做更让人意外
            SetupHint = "语音输入要用模型服务的 Key 做转写：先在设置里填 API Key，这段文字会留在这里。";
            RequestFocus();
            return;
        }

        try
        {
            VoiceError = null;

            if (!IsRecording)
            {
                await _voice.StartAsync();
                IsRecording = true;
                return;
            }

            await StopRecordingAndTranscribeAsync();
        }
        catch (Exception ex)
        {
            IsRecording = false;
            VoiceError = DescribeVoiceError(ex);
        }
    }

    /// <summary>
    /// 录音上限。超过两分钟转写会更慢、更容易失败，收益也不大；
    /// 自动停下并把已经录到的转出来，比让用户一直录到爆更合理。
    /// </summary>
    private static readonly TimeSpan MaxRecordingDuration = TimeSpan.FromMinutes(2);

    private async Task StopRecordingAndTranscribeAsync()
    {
        if (!IsRecording)
        {
            return;
        }

        IsRecording = false;
        IsTranscribing = true;
        UpdateActivity();

        var transcribed = false;
        try
        {
            var text = await _voice.StopAndTranscribeAsync();
            if (string.IsNullOrWhiteSpace(text))
            {
                VoiceError = "这段录音里没有识别到说话声：靠近麦克风再说一次，或直接用键盘输入。";
                return;
            }

            Text = string.IsNullOrWhiteSpace(Text) ? text : $"{Text.TrimEnd()} {text}";
            transcribed = true;
        }
        finally
        {
            IsTranscribing = false;

            var queued = _sendAfterTranscribing;
            _sendAfterTranscribing = false;
            UpdateActivity();

            // 转写没成功就不发：那一下 Enter 想发的是「刚说的话」，不是输入框里剩下的旧内容
            if (queued && transcribed && SendCommand.CanExecute(null))
            {
                await SendAsync();
            }
        }
    }

    /// <summary>
    /// 把转写的原始异常翻成一句能照做的话。以前这里直接显示 ex.Message，
    /// 在不支持 /audio/transcriptions 的端点上，用户看到的是一串英文堆栈。
    /// </summary>
    private static string DescribeVoiceError(Exception ex)
    {
        var raw = ex.Message ?? string.Empty;

        if (Contains(raw, "404", "not found", "unsupported", "audio/transcriptions", "transcription"))
        {
            return "这个端点不支持语音转写（/audio/transcriptions）。换一个支持的服务商，或直接用键盘输入。";
        }

        if (Contains(raw, "401", "unauthorized", "invalid api key", "api key"))
        {
            return "转写用的 Key 没通过：去设置里确认 Key 和端点是不是同一家。";
        }

        if (Contains(raw, "麦克风", "microphone", "MMDevice", "device not found", "access is denied", "0x80070005", "拒绝访问"))
        {
            return "没拿到麦克风：可能被别的程序占用，或系统设置里关掉了这个应用的麦克风权限。";
        }

        if (Contains(raw, "timeout", "timed out", "taskcanceled", "超时"))
        {
            return "转写超时了：检查网络或代理，再录一次（这次可以短一点）。";
        }

        if (Contains(raw, "no speech", "silence", "empty", "没有检测到"))
        {
            return "这段录音里没有识别到说话声：靠近麦克风再说一次。";
        }

        return "语音没转成：" + raw;
    }

    private static bool Contains(string text, params string[] keys)
        => keys.Any(key => text.Contains(key, StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private async Task AttachAsync()
    {
        // 一次可以选多个；每个都变成引用标签，和 @ 引用走同一条「发送时读正文」的链路，
        // 不再是往正文里塞一行路径让模型自己去读。
        var files = await _picker.PickFilesAsync("选择要交给助手的文件");
        if (files.Count == 0)
        {
            return;
        }

        foreach (var path in files)
        {
            AddReference(MentionItemViewModel.FromFile(path));
        }

        // 选完文件光标应该还在输入框末尾，不该停在纸夹按钮上。
        RequestFocus();
    }

    public void RequestFocus() => FocusRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// 切到另一套模型配置。切的是一整套：地址、Key、协议跟着一起换，
    /// 所以下一轮会自动重建会话（见 PiAgentRuntime 里 SessionHolder 的复用条件）。
    /// </summary>
    public async Task SetModelAsync(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        var settings = _settings.Current;
        if (!settings.ActivateModel(profileId))
        {
            return;
        }

        Refresh();
        await _settings.SaveAsync(settings);
        RequestFocus();
    }

    public async Task SetPermissionAsync(PermissionMode permission)
    {
        if (_settings.Current.Permission == permission)
        {
            return;
        }

        var previous = _settings.Current.Permission;
        _settings.Current.Permission = permission;
        Refresh();
        PermissionChanged?.Invoke(this, permission);

        try
        {
            await _settings.SaveAsync(_settings.Current);
        }
        catch (Exception ex)
        {
            // 写盘失败要把档位退回去：界面停在「已切换」而磁盘还是旧值，下次启动就变了卦。
            // 这里也吞不得异常——命令是从 async void 事件里调进来的，抛出去会直接崩进程。
            _settings.Current.Permission = previous;
            Refresh();
            PermissionChanged?.Invoke(this, previous);
            SettingsWriteFailed?.Invoke(this, "权限没保存成功：" + ex.Message);
        }
        finally
        {
            RequestFocus();
        }
    }

    /// <summary>浮层里的「只用只读」快捷入口。</summary>
    [RelayCommand]
    private async Task UseReadOnlyAsync() => await SetPermissionAsync(PermissionMode.ReadOnly);

    /// <summary>切回默认权限。</summary>
    [RelayCommand]
    private async Task UseDefaultPermissionAsync() => await SetPermissionAsync(PermissionMode.Workspace);

    public void Dispose()
    {
        _settings.Changed -= _settingsChanged;
        StopRecordingTicker();
        _imageErrorTimer?.Stop();
        _imageErrorTimer = null;
    }

    partial void OnIsSendingChanged(bool value) => UpdateActivity();

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanUseVoice));
        OnPropertyChanged(nameof(MicTooltip));
    }

    partial void OnIsRecordingChanged(bool value)
    {
        if (value)
        {
            StartRecordingTicker();
        }
        else
        {
            StopRecordingTicker();
        }

        UpdateActivity();
    }

    private DispatcherTimer? _recordingTicker;
    private DateTimeOffset _recordingStartedAt;

    private void StartRecordingTicker()
    {
        _recordingStartedAt = DateTimeOffset.Now;
        _recordingTicker?.Stop();
        _recordingTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _recordingTicker.Tick += async (_, _) =>
        {
            if (DateTimeOffset.Now - _recordingStartedAt >= MaxRecordingDuration)
            {
                StopRecordingTicker();
                try
                {
                    await StopRecordingAndTranscribeAsync();
                }
                catch (Exception ex)
                {
                    IsRecording = false;
                    VoiceError = DescribeVoiceError(ex);
                }

                return;
            }

            UpdateActivity();
        };
        _recordingTicker.Start();
    }

    private void StopRecordingTicker()
    {
        _recordingTicker?.Stop();
        _recordingTicker = null;
    }

    /// <summary>把「正在等什么」写到输入框下面。读大文件、录音这两件事以前在界面上是无声的。</summary>
    private void UpdateActivity()
    {
        if (IsRecording)
        {
            var elapsed = DateTimeOffset.Now - _recordingStartedAt;
            var left = MaxRecordingDuration - elapsed;
            ActivityText = left <= TimeSpan.FromSeconds(20)
                ? $"正在录音 {elapsed:mm\\:ss} · 还剩 {Math.Max(0, (int)left.TotalSeconds)} 秒自动转写"
                : $"正在录音 {elapsed:mm\\:ss} / 最长 2:00 · 再点麦克风结束，Esc 取消";
        }
        else if (IsTranscribing)
        {
            ActivityText = _sendAfterTranscribing
                ? "正在转写…转完自动发送"
                : "正在转写这段录音，转到输入框里…（Enter 转完自动发）";
        }
        else if (IsSending)
        {
            ActivityText = References.Count > 0
                ? _sendAfterReading
                    ? $"正在读取 {References.Count} 份引用…读完就发"
                    : $"正在读取 {References.Count} 份引用…"
                : "正在准备…";
        }
        else
        {
            ActivityText = null;
        }
    }

    public void Refresh()
    {
        // 设置保存的 Changed 是从保存所在的线程上抛出来的（SaveAsync 内部全程 ConfigureAwait(false)），
        // 这里直接往下走就会用后台线程清 Models、触发命令的 CanExecuteChanged，
        // 绑定的按钮一读 Command 属性就是「该对象归另一个线程所有」。统一回 UI 线程再刷。
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Ui.Post(Refresh);
            return;
        }

        // 工作区或设置变了，下次 @ 要重新扫盘，别拿上一个工作区的文件
        _indexed = false;
        RefreshModels();

        // 配好了就把「还没配模型服务」的提示收掉。
        if (IsConfigured)
        {
            SetupHint = null;
        }

        OnPropertyChanged(nameof(ModelLabel));
        OnPropertyChanged(nameof(CurrentProfileId));
        OnPropertyChanged(nameof(ModelTooltip));
        OnPropertyChanged(nameof(PermissionLabel));
        OnPropertyChanged(nameof(Permission));
        OnPropertyChanged(nameof(PermissionDetail));
        OnPropertyChanged(nameof(PermissionTooltip));
        OnPropertyChanged(nameof(IsFullAccess));
        OnPropertyChanged(nameof(IsReadOnlyMode));
        OnPropertyChanged(nameof(PermissionForeground));
        OnPropertyChanged(nameof(ShowFullAccessNotice));
        OnPropertyChanged(nameof(WorkspaceLabel));
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(WorkspacePathHint));
        OnPropertyChanged(nameof(IsConfigured));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>
    /// 列表只来自设置里配的那几套。以前这里会给「当前模型不在列表里」补一条「当前配置」，
    /// 现在当前模型本身就是列表里的一项，补出来的那条只会是重复项。
    /// </summary>
    private void RefreshModels()
    {
        Models.Clear();
        foreach (var model in _runtime.Models)
        {
            Models.Add(model);
        }
    }
}
