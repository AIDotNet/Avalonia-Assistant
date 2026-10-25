using System.Collections.ObjectModel;
using Avalonia.Media;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed partial class ComposerViewModel : ObservableObject, IDisposable
{
    private readonly IAssistantRuntime _runtime;
    private readonly ISettingsProvider _settings;
    private readonly IFilePicker _picker;
    private readonly Func<Task> _openSettings;
    private readonly IVoiceInput _voice;
    private readonly EventHandler _settingsChanged;

    /// <summary>是否已绑定到会话。false 时跟随全局设置（首页就是这样）。</summary>
    private bool _workspaceBound;

    /// <summary>会话自己的工作区；为 null 表示这个任务没有工作区（普通对话）。</summary>
    private string? _boundWorkspace;

    public ComposerViewModel(
        IAssistantRuntime runtime,
        ISettingsProvider settings,
        IFilePicker picker,
        IVoiceInput voice,
        Func<Task> openSettings)
    {
        _runtime = runtime;
        _settings = settings;
        _picker = picker;
        _voice = voice;
        _openSettings = openSettings;
        _settingsChanged = (_, _) => Refresh();
        RefreshModels();
        _settings.Changed += _settingsChanged;
    }

    public event EventHandler<string>? SendRequested;

    public event EventHandler? FocusRequested;

    public event EventHandler? StopRequested;

    /// <summary>权限档位变化。对话页据此插一条提示，让用户知道确认行为变了。</summary>
    public event EventHandler<PermissionMode>? PermissionChanged;

    /// <summary>工作区换了。对话页要跟着改会话自己的工作区，否则界面显示新目录、工具还在旧目录里跑。</summary>
    public event EventHandler<string>? WorkspaceChanged;

    public ObservableCollection<ModelOption> Models { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string? text;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSend))]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    [NotifyPropertyChangedFor(nameof(CanChangePermission))]
    [NotifyPropertyChangedFor(nameof(PermissionTooltip))]
    [NotifyPropertyChangedFor(nameof(IsMicEnabled))]
    [NotifyPropertyChangedFor(nameof(MicTooltip))]
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

    /// <summary>录音已经结束、正在等转写结果。这期间既不能发送，也不该让人以为界面卡住了。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(MicTooltip))]
    [NotifyPropertyChangedFor(nameof(IsMicEnabled))]
    private bool isTranscribing;

    public bool HasVoiceError => !string.IsNullOrWhiteSpace(VoiceError);

    public string MicTooltip => IsTranscribing
        ? "正在转写，稍候…"
        : IsRecording
            ? "停止并转写"
            : "语音输入";

    /// <summary>转写期间禁掉麦克风，避免连点重复提交。</summary>
    public bool IsMicEnabled => !IsTranscribing && !IsBusy;

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

    public string PermissionTooltip => IsBusy
        ? "正在运行，本轮结束后才能切换工具权限"
        : $"工具权限：{PermissionLabel}";

    /// <summary>
    /// 运行中不给切档。这一轮的授权钩子和工具集在建会话时就按当时的档位定死了，
    /// 中途改设置只会让界面显示新档位、实际还按旧档位跑。
    /// </summary>
    public bool CanChangePermission => !IsBusy;

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

    public string WorkspaceLabel => ShortWorkspace(WorkspacePath);

    /// <summary>
    /// 当前输入框指向的工作区。对话页绑会话自己的路径，首页不绑、跟随全局设置。
    /// 之前两种场景都读全局设置：从侧栏切了工作区，胶囊显示新目录，而这一轮仍然写在旧目录里。
    /// </summary>
    public string WorkspacePath => _workspaceBound
        ? _boundWorkspace ?? string.Empty
        : _settings.Current.WorkspacePath;

    /// <summary>对话页创建时调用：把显示和切换都固定到会话自己的工作区上。</summary>
    public void BindWorkspace(string? path)
    {
        _workspaceBound = true;
        _boundWorkspace = string.IsNullOrWhiteSpace(path) ? null : path;
        OnPropertyChanged(nameof(WorkspaceLabel));
        OnPropertyChanged(nameof(WorkspacePath));
    }

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

        Text = string.Empty;
        SendRequested?.Invoke(this, value);
    }

    private bool CanSend() => !IsBusy && !IsRecording && !IsTranscribing && !string.IsNullOrWhiteSpace(Text);

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
        if (IsBusy || IsTranscribing)
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
            IsTranscribing = true;

            string text;
            try
            {
                text = await _voice.StopAndTranscribeAsync();
            }
            finally
            {
                IsTranscribing = false;
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                Text = string.IsNullOrWhiteSpace(Text) ? text : $"{Text.TrimEnd()} {text}";
            }
        }
        catch (Exception ex)
        {
            IsRecording = false;
            IsTranscribing = false;
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
        // 只有对话页是绑定的；首页跟着全局设置走，不能因为选过一次目录就锁死。
        if (_workspaceBound)
        {
            _boundWorkspace = folder;
        }

        var settings = _settings.Current;
        settings.WorkspacePath = folder;
        settings.RecentWorkspaces.RemoveAll(path => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase));
        settings.RecentWorkspaces.Insert(0, folder);
        Refresh();
        WorkspaceChanged?.Invoke(this, folder);
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
