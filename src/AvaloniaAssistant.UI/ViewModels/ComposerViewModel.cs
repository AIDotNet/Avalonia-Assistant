using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
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

    public event EventHandler? StopRequested;

    /// <summary>权限档位变化。对话页据此插一条提示，让用户知道确认行为变了。</summary>
    public event EventHandler<PermissionMode>? PermissionChanged;

    public ObservableCollection<ModelOption> Models { get; } = [];

    /// <summary>这一轮 @ 进来的文件，发送时变成 <see cref="PromptRequest.Attachments"/>。</summary>
    public ObservableCollection<MentionItemViewModel> References { get; } = [];

    /// <summary>@ 浮层里的候选，最多 8 条。</summary>
    public ObservableCollection<MentionItemViewModel> MentionItems { get; } = [];

    /// <summary>待发送的图片（粘贴进来的）。</summary>
    public ObservableCollection<PendingImageViewModel> Images { get; } = [];

    public bool HasImages => Images.Count > 0;

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

    public bool HasQueued => !string.IsNullOrWhiteSpace(QueuedHint);

    /// <summary>
    /// 生成期间按 Enter 不是"没反应"，是排队。把这件事写在原来那行提示里，
    /// 用户不用先按一次才发现行为变了。
    /// </summary>
    public string InputHint => IsBusy
        ? "Enter 排队，本轮结束后自动发送"
        : "Enter 发送 · Shift+Enter 换行 · @ 引用文件";

    /// <summary>正在读引用文件的正文，读完才真正发出。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private bool isSending;

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

    /// <summary>权限浮层里的「恢复逐次确认」，由对话页负责真正清空放行记录。</summary>
    public event EventHandler? ResetSessionAllowedRequested;

    [RelayCommand]
    private void ResetSessionAllowed() => ResetSessionAllowedRequested?.Invoke(this, EventArgs.Empty);

    public bool HasVoiceError => !string.IsNullOrWhiteSpace(VoiceError);

    public string MicTooltip => IsRecording ? "停止并转写" : "语音输入";

    public IBrush MicBrush => IsRecording
        ? new SolidColorBrush(Color.Parse("#B3261E"))
        : new SolidColorBrush(Color.Parse("#6F6F69"));

    public bool ShowStop => IsBusy;

    public string Placeholder => "描述你要做的事，越具体越好";

    public string ModelLabel => _settings.Current.Model;

    public string PermissionLabel => PermissionModeText.Describe(_settings.Current.Permission);

    /// <summary>当前档位，权限浮层据此画选中态。</summary>
    public PermissionMode Permission => _settings.Current.Permission;

    /// <summary>权限说明，展示在权限浮层顶部。</summary>
    public string PermissionDetail => PermissionModeText.Detail(_settings.Current.Permission);

    public string PermissionTooltip => HasSessionAllowedTools
        ? $"工具权限：{PermissionLabel}（本次已放行 {SessionAllowedCount} 个工具）"
        : $"工具权限：{PermissionLabel}";

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

    public string WorkspaceLabel
    {
        get
        {
            var name = WorkspaceNaming.ShortName(_settings.Current.WorkspacePath);
            return name.Length > 0 ? name : "普通任务";
        }
    }

    public string WorkspacePath => _settings.Current.WorkspacePath;

    /// <summary>
    /// 胶囊的悬停提示。普通任务也有落点（用户目录下的 .avaloniaassistant），
    /// 得说出来——不然让它写的报告到底存哪了，只能靠猜。
    /// </summary>
    public string WorkspacePathHint => string.IsNullOrWhiteSpace(WorkspacePath)
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

            Text = string.Empty;
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
    private bool CanSend() => !IsSending && !IsRecording
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

    private bool AddReference(MentionItemViewModel item)
    {
        if (References.Any(reference => reference.Id == item.Id))
        {
            return false;
        }

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
            bitmap.Save(path);

            var image = new ChatImage
            {
                Id = id,
                Path = path,
                MimeType = "image/png",
                Width = bitmap.PixelSize.Width,
                Height = bitmap.PixelSize.Height,
                Size = new FileInfo(path).Length,
            };

            Images.Add(new PendingImageViewModel(image, bitmap, RemoveImage));
            OnPropertyChanged(nameof(HasImages));
            SendCommand.NotifyCanExecuteChanged();
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
            _ = _openSettings();
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

            IsRecording = false;
            var text = await _voice.StopAndTranscribeAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                Text = string.IsNullOrWhiteSpace(Text) ? text : $"{Text.TrimEnd()} {text}";
            }
        }
        catch (Exception ex)
        {
            IsRecording = false;
            VoiceError = ex.Message;
        }
    }

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

    public async Task SetModelAsync(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return;
        }

        _settings.Current.Model = modelId;
        Refresh();
        await _settings.SaveAsync(_settings.Current);
        RequestFocus();
    }

    public async Task SetPermissionAsync(PermissionMode permission)
    {
        if (_settings.Current.Permission == permission)
        {
            return;
        }

        _settings.Current.Permission = permission;
        Refresh();
        PermissionChanged?.Invoke(this, permission);
        await _settings.SaveAsync(_settings.Current);
        RequestFocus();
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
    }

    partial void OnIsSendingChanged(bool value) => UpdateActivity();

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
        _recordingTicker.Tick += (_, _) => UpdateActivity();
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
            ActivityText = $"正在录音 {elapsed:mm\\:ss} · 再点麦克风结束，Esc 取消";
        }
        else if (IsSending)
        {
            ActivityText = References.Count > 0
                ? $"正在读取 {References.Count} 份引用…"
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

    private void RefreshModels()
    {
        var current = _settings.Current.Model;
        Models.Clear();
        foreach (var model in _runtime.Models)
        {
            Models.Add(model);
        }

        if (!Models.Any(model => model.Id == current))
        {
            Models.Insert(0, new ModelOption(current, current, "当前配置", string.Empty));
        }
    }
}
