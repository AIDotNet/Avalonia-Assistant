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
    private IReadOnlyList<LibraryDocument> _documents = [];
    private string? _documentsWorkspace;

    /// <summary>自己在改 Text（补全 @、清空）时不要再弹一次浮层。</summary>
    private bool _suppressMention;

    /// <summary>资料库候选的加载序号：并发输入时只认最后一次。</summary>
    private int _mentionVersion;

    private bool _documentsLoaded;

    public ComposerViewModel(
        IAssistantRuntime runtime,
        ISettingsProvider settings,
        IFilePicker picker,
        IVoiceInput voice,
        Func<Task> openSettings,
        LibraryStore library)
    {
        _runtime = runtime;
        _settings = settings;
        _picker = picker;
        _voice = voice;
        _openSettings = openSettings;
        _library = library;
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

    /// <summary>这一轮 @ 进来的资料，发送时变成 <see cref="PromptRequest.Attachments"/>。</summary>
    public ObservableCollection<LibraryItemViewModel> References { get; } = [];

    /// <summary>@ 浮层里的候选，最多 6 条。</summary>
    public ObservableCollection<LibraryItemViewModel> MentionItems { get; } = [];

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
    private void Send()
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

        var attachments = References
            .Select(reference => new ChatAttachment
            {
                Id = reference.Document.Id,
                Name = reference.Document.Name,
                Kind = reference.Document.Kind,
                Size = reference.Document.Size,
                Text = reference.Document.Text,
            })
            .ToList();

        Text = string.Empty;
        References.Clear();
        OnPropertyChanged(nameof(HasReferences));
        CloseMention();

        SendRequested?.Invoke(this, new PromptRequest(value, attachments));
    }

    private bool CanSend() => !IsBusy && !IsRecording && !string.IsNullOrWhiteSpace(Text);

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
        var items = _documents
            .Where(document => LibraryStore.Matches(document, query))
            .OrderByDescending(document => document.AddedAt)
            .Take(6)
            .ToList();

        MentionItems.Clear();
        foreach (var document in items)
        {
            MentionItems.Add(new LibraryItemViewModel(document));
        }

        MentionIndex = items.Count > 0 ? 0 : -1;
        ApplyMentionSelection();
        MentionEmptyText = _documents.Count == 0
            ? "资料库还是空的。去「资料库」把文件拖进去，之后在这里 @ 就能引用。"
            : $"资料库里没有带「{query}」的内容。";
        ShowMention = true;
        OnPropertyChanged(nameof(HasMentionItems));
    }

    private async Task EnsureDocumentsAsync()
    {
        var workspace = _settings.Current.WorkspacePath ?? string.Empty;
        if (_documentsLoaded && string.Equals(_documentsWorkspace, workspace, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _documents = await _library.LoadAsync(workspace);
        _documentsWorkspace = workspace;
        _documentsLoaded = true;
    }

    /// <summary>
    /// 按名字引用一份资料，不改输入框文字。给自动化验收用，
    /// 将来资料库页做「引用到对话」也走这里。
    /// </summary>
    public async Task<bool> ReferenceByNameAsync(string name)
    {
        await EnsureDocumentsAsync();

        var document = _documents.FirstOrDefault(item =>
            item.Name.Contains(name, StringComparison.OrdinalIgnoreCase));

        if (document is null)
        {
            return false;
        }

        if (References.All(reference => reference.Document.Id != document.Id))
        {
            References.Add(new LibraryItemViewModel(document));
            OnPropertyChanged(nameof(HasReferences));
        }

        return true;
    }

    [RelayCommand]
    private void AcceptMention(LibraryItemViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        var document = item.Document;
        var text = Text ?? string.Empty;
        var match = MentionPattern.Match(text);
        var replacement = "@" + document.Name + " ";

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

        if (References.All(reference => reference.Document.Id != document.Id))
        {
            References.Add(item);
            OnPropertyChanged(nameof(HasReferences));
        }

        CloseMention();
    }

    [RelayCommand]
    private void RemoveReference(LibraryItemViewModel? item)
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
