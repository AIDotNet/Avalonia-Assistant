using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using Avalonia.Media;
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

    private readonly LibraryStore _library;
    private readonly WorkspaceFileSearch _fileSearch;
    private IReadOnlyList<LibraryDocument> _documents = [];
    private string? _documentsWorkspace;

    /// <summary>自己在改 Text（补全 @、清空）时不要再弹一次浮层。</summary>
    private bool _suppressMention;

    /// <summary>@ 候选的加载序号：并发输入时只认最后一次。</summary>
    private int _mentionVersion;

    private bool _documentsLoaded;

    public ComposerViewModel(
        IAssistantRuntime runtime,
        ISettingsProvider settings,
        IFilePicker picker,
        IVoiceInput voice,
        Func<Task> openSettings,
        LibraryStore library,
        WorkspaceFileSearch fileSearch)
    {
        _runtime = runtime;
        _settings = settings;
        _picker = picker;
        _voice = voice;
        _openSettings = openSettings;
        _library = library;
        _fileSearch = fileSearch;
        _settingsChanged = (_, _) => Refresh();
        RefreshModels();
        _settings.Changed += _settingsChanged;
    }

    public event EventHandler<PromptRequest>? SendRequested;

    public event EventHandler? FocusRequested;

    public event EventHandler? StopRequested;

    /// <summary>权限档位变化。对话页据此插一条提示，让用户知道确认行为变了。</summary>
    public event EventHandler<PermissionMode>? PermissionChanged;

    public ObservableCollection<ModelOption> Models { get; } = [];

    /// <summary>这一轮 @ 进来的文件（工作区或资料库），发送时变成 <see cref="PromptRequest.Attachments"/>。</summary>
    public ObservableCollection<MentionItemViewModel> References { get; } = [];

    /// <summary>@ 浮层里的候选，最多 8 条。</summary>
    public ObservableCollection<MentionItemViewModel> MentionItems { get; } = [];

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
    private bool isBusy;

    public bool ShowSend => !IsBusy;

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

    public string PermissionTooltip => $"工具权限：{PermissionLabel}";

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

    public string WorkspaceLabel => ShortWorkspace(_settings.Current.WorkspacePath);

    public string WorkspacePath => _settings.Current.WorkspacePath;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Current.ApiKey);

    public IReadOnlyList<string> RecentWorkspaces => _settings.Current.RecentWorkspaces;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        if (IsRecording)
        {
            CancelRecording();
        }

        var value = Text?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return;
        }

        if (!IsConfigured)
        {
            _ = _openSettings();
            return;
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

            Text = string.Empty;
            References.Clear();
            OnPropertyChanged(nameof(HasReferences));
            CloseMention();

            SendRequested?.Invoke(this, new PromptRequest(value, attachments));
        }
        finally
        {
            IsSending = false;
        }
    }

    /// <summary>把一个引用变成模型能读的附件：工作区读盘、资料库直接用索引里的正文。</summary>
    private static async Task<ChatAttachment> BuildAttachmentAsync(MentionItemViewModel item)
    {
        var attachment = new ChatAttachment
        {
            Id = item.Id,
            Name = item.Name,
            Path = item.Path,
            Source = item.IsLibrary ? "library" : "workspace",
            Kind = item.Kind,
            Size = item.Size,
        };

        if (item.Document is { } document)
        {
            attachment.Text = document.Text;
            attachment.LineCount = document.HasText ? document.Text!.Count(character => character == '\n') + 1 : 0;
            attachment.Truncated = document.HasText && document.Text!.Length >= LibraryStore.MaxIndexedChars;
            return attachment;
        }

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

    private bool CanSend() => !IsBusy && !IsSending && !IsRecording && !string.IsNullOrWhiteSpace(Text);

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
        _ = RefreshMentionAsync();
    }

    private async Task RefreshMentionAsync()
    {
        var version = ++_mentionVersion;

        await EnsureDocumentsAsync();

        // 等待读索引的这段时间用户又敲了字，这一次结果已经过期
        if (version != _mentionVersion)
        {
            return;
        }

        var query = MentionQuery;
        var workspace = _settings.Current.WorkspacePath ?? string.Empty;

        // 工作区要扫盘，丢线程池；资料库索引本来就在内存里
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

        // 同名的资料库条目排在后面一点点：用户多半指的是眼前工作区里那份
        foreach (var document in _documents
                     .Where(document => LibraryStore.Matches(document, query))
                     .OrderByDescending(document => document.AddedAt)
                     .Take(6))
        {
            var score = query.Length == 0
                ? 100 + rank++
                : WorkspaceFileSearch.Score(document.Name, query) + 5;
            candidates.Add((MentionItemViewModel.FromLibrary(document), score));
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

    private string BuildMentionEmptyText(string workspace, string query)
    {
        if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace))
        {
            return "还没有工作区。选一个文件夹，@ 才能在工作区里找文件。";
        }

        return query.Length > 0
            ? $"工作区和资料库里都没有带「{query}」的文件。"
            : "工作区里还没有可引用的文件。";
    }

    private async Task EnsureDocumentsAsync()
    {
        var workspace = _settings.Current.WorkspacePath ?? string.Empty;
        if (_documentsLoaded && string.Equals(_documentsWorkspace, workspace, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _documents = await _library.LoadAsync(workspace);

        // 顺手在工作区预热一遍文件索引，别让用户第一次按 @ 时等扫盘
        if (!string.IsNullOrWhiteSpace(workspace) && Directory.Exists(workspace))
        {
            await Task.Run(() => _fileSearch.List(workspace));
        }

        _documentsWorkspace = workspace;
        _documentsLoaded = true;
    }

    /// <summary>
    /// 按名字引用一个文件，不改输入框文字。给自动化验收用，
    /// 将来资料库页做「引用到对话」也走这里。先找工作区，再找资料库。
    /// </summary>
    public async Task<bool> ReferenceByNameAsync(string name)
    {
        await EnsureDocumentsAsync();

        var workspace = _settings.Current.WorkspacePath ?? string.Empty;
        var file = _fileSearch
            .Search(workspace, name, 8)
            .FirstOrDefault(entry => entry.RelativePath.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (file is not null)
        {
            AddReference(MentionItemViewModel.FromWorkspace(file));
            return true;
        }

        var document = _documents.FirstOrDefault(item =>
            item.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (document is null)
        {
            return false;
        }

        AddReference(MentionItemViewModel.FromLibrary(document));
        return true;
    }

    private void AddReference(MentionItemViewModel item)
    {
        if (References.Any(reference => reference.Id == item.Id))
        {
            return;
        }

        References.Add(item);
        OnPropertyChanged(nameof(HasReferences));
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
        var file = await _picker.PickFileAsync("选择要交给助手的文件");
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        Text = string.IsNullOrWhiteSpace(Text)
            ? $"请阅读这个文件并帮我处理：{file}"
            : $"{Text.TrimEnd()}\n\n文件：{file}";
    }

    [RelayCommand]
    private async Task PickWorkspaceAsync()
    {
        var folder = await _picker.PickFolderAsync("选择工作区");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        await SetWorkspaceAsync(folder);
    }

    public async Task SetWorkspaceAsync(string folder)
    {
        SetWorkspace(folder);
        await _settings.SaveAsync(_settings.Current);
    }

    public void SetWorkspace(string folder)
    {
        var settings = _settings.Current;
        settings.WorkspacePath = folder;
        settings.RecentWorkspaces.RemoveAll(path => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase));
        settings.RecentWorkspaces.Insert(0, folder);
        Refresh();
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
    }

    /// <summary>浮层里的「只用只读」快捷入口。</summary>
    [RelayCommand]
    private async Task UseReadOnlyAsync() => await SetPermissionAsync(PermissionMode.ReadOnly);

    /// <summary>切回默认权限。</summary>
    [RelayCommand]
    private async Task UseDefaultPermissionAsync() => await SetPermissionAsync(PermissionMode.Workspace);

    public void Dispose() => _settings.Changed -= _settingsChanged;

    public void Refresh()
    {
        // 工作区或设置变了，下次 @ 要重新读索引，别拿上一个工作区的资料
        _documentsLoaded = false;
        RefreshModels();
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

    private static string ShortWorkspace(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return "普通任务";
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
