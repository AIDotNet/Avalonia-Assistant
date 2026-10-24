using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaAssistant.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed partial class ProviderOptionViewModel : ObservableObject
{
    private readonly Action<ProviderOptionViewModel> _select;

    public ProviderOptionViewModel(ProviderPreset preset, Action<ProviderOptionViewModel> select)
    {
        Preset = preset;
        _select = select;
    }

    public ProviderPreset Preset { get; }

    public string Name => Preset.Name;

    public string Description => Preset.Description;

    [ObservableProperty]
    private bool isSelected;

    [RelayCommand]
    private void Select() => _select(this);
}

public sealed partial class SettingsViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _host;

    /// <summary>进页面时的快照。用来判断用户改过没有——之前改完点左上角返回，改动会静默消失。</summary>
    private AssistantSettings _baseline;

    public SettingsViewModel(MainWindowViewModel host)
    {
        _host = host;
        Settings = host.Settings.Clone();
        _baseline = Settings.Clone();

        // Ctrl+滚轮在设置页也能用：档位高亮要跟着实际缩放走，不能停在进页面时的快照上。
        _host.FontScaleChanged += OnHostFontScaleChanged;

        Providers = new ObservableCollection<ProviderOptionViewModel>(
            ProviderPreset.All.Select(preset => new ProviderOptionViewModel(preset, SelectProvider)
            {
                IsSelected = preset.Kind == Settings.Provider,
            }));

        SavedText = string.Empty;
    }

    public void Dispose() => _host.FontScaleChanged -= OnHostFontScaleChanged;

    private void OnHostFontScaleChanged(object? sender, EventArgs e)
    {
        Settings.FontScale = _host.FontScale;
        NotifyScaleChanged();
    }

    public AssistantSettings Settings { get; }

    public ObservableCollection<ProviderOptionViewModel> Providers { get; }

    public string ApiKey
    {
        get => Settings.ApiKey;
        set
        {
            Settings.ApiKey = value;
            OnPropertyChanged();
            Touch();
            ClearTestResult();
        }
    }

    public string BaseUrl
    {
        get => Settings.BaseUrl;
        set
        {
            Settings.BaseUrl = value;
            OnPropertyChanged();
            Touch();
            ClearTestResult();
        }
    }

    public string Model
    {
        get => Settings.Model;
        set
        {
            Settings.Model = value;
            OnPropertyChanged();
            Touch();
            ClearTestResult();
        }
    }

    public double Temperature
    {
        get => Settings.Temperature;
        set
        {
            Settings.Temperature = value;
            OnPropertyChanged();
            Touch();
        }
    }

    public int MaxTokens
    {
        get => Settings.MaxTokens;
        set
        {
            Settings.MaxTokens = value;
            OnPropertyChanged();
            Touch();
        }
    }

    /// <summary>有没有还没保存的改动。返回按钮和底部提示都看它。</summary>
    public bool HasUnsavedChanges =>
        Settings.Provider != _baseline.Provider
        || Settings.Permission != _baseline.Permission
        || Settings.Protocol != _baseline.Protocol
        || Settings.MaxTokens != _baseline.MaxTokens
        || Math.Abs(Settings.Temperature - _baseline.Temperature) > 0.0001
        || !string.Equals(Settings.ApiKey, _baseline.ApiKey, StringComparison.Ordinal)
        || !string.Equals(Settings.BaseUrl, _baseline.BaseUrl, StringComparison.Ordinal)
        || !string.Equals(Settings.Model, _baseline.Model, StringComparison.Ordinal)
        || !string.Equals(Settings.WorkspacePath, _baseline.WorkspacePath, StringComparison.OrdinalIgnoreCase);

    /// <summary>点了返回但还有改动：底部换成「保存并返回 / 放弃改动」，别一声不响地丢掉。</summary>
    [ObservableProperty]
    private bool showLeavePrompt;

    private void Touch() => OnPropertyChanged(nameof(HasUnsavedChanges));

    /// <summary>改了和连接有关的字段，上一次的测试结论就不作数了。</summary>
    private void ClearTestResult()
    {
        if (TestResultText is not null)
        {
            TestResultText = null;
        }
    }


    public string WorkspacePath => Settings.WorkspacePath;

    public bool IsChatProtocolSelected => Settings.EffectiveProtocol == AgentProtocol.OpenAiCompatible;

    public bool IsResponsesProtocolSelected => Settings.EffectiveProtocol == AgentProtocol.OpenAiResponses;

    public bool IsMessagesProtocolSelected => Settings.EffectiveProtocol == AgentProtocol.AnthropicMessages;

    /// <summary>协议说明。没手动选过时补一句“跟随服务商推荐”，避免以为是写死的。</summary>
    public string ProtocolHint => Settings.Protocol is null
        ? Settings.ProtocolInfo.Description + "（当前跟随服务商推荐）"
        : Settings.ProtocolInfo.Description;

    public bool IsReadOnlySelected => Settings.Permission == PermissionMode.ReadOnly;

    public bool IsWorkspaceSelected => Settings.Permission == PermissionMode.Workspace;

    public bool IsFullAccessSelected => Settings.Permission == PermissionMode.FullAccess;

    // ---- 界面与启动 ----

    public bool IsScale90 => NearScale(0.9);

    public bool IsScale100 => NearScale(1.0);

    public bool IsScale110 => NearScale(1.1);

    public bool IsScale125 => NearScale(1.25);

    public string ScaleText => $"当前 {Math.Round(_host.FontScale * 100)}%";

    public bool IsStartupHome => !IsStartupLast;

    public bool IsStartupLast => string.Equals(Settings.StartupPage, "last", StringComparison.OrdinalIgnoreCase);

    private bool NearScale(double value) => Math.Abs(_host.FontScale - value) < 0.001;

    /// <summary>
    /// 缩放和启动页属于「看一眼就该生效」的偏好，改完立刻落盘，
    /// 不进「改动还没保存」那套提示——否则用户会以为还得再点一次保存。
    /// </summary>
    [RelayCommand]
    private async Task SetScaleAsync(string value)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var scale))
        {
            return;
        }

        Settings.FontScale = scale;
        _baseline.FontScale = scale;
        _host.SetFontScale(scale, persist: false);
        NotifyScaleChanged();
        await _host.SaveSettingsQuietlyAsync();
    }

    [RelayCommand]
    private async Task SetStartupAsync(string value)
    {
        var page = string.Equals(value, "last", StringComparison.OrdinalIgnoreCase) ? "last" : "home";
        Settings.StartupPage = page;
        _baseline.StartupPage = page;
        _host.Settings.StartupPage = page;
        OnPropertyChanged(nameof(IsStartupHome));
        OnPropertyChanged(nameof(IsStartupLast));
        await _host.SaveSettingsQuietlyAsync();
    }

    private void NotifyScaleChanged()
    {
        OnPropertyChanged(nameof(IsScale90));
        OnPropertyChanged(nameof(IsScale100));
        OnPropertyChanged(nameof(IsScale110));
        OnPropertyChanged(nameof(IsScale125));
        OnPropertyChanged(nameof(ScaleText));
    }

    /// <summary>
    /// 保存表单前，把「窗口位置、侧栏宽度、最后会话」这些开着设置页时仍在变的字段
    /// 从实时设置抄回来。否则在设置页里拖过窗口再点保存，会把旧尺寸写回去。
    /// </summary>
    private void SyncWindowPreferences()
    {
        var live = _host.Settings;
        Settings.IsSidebarVisible = live.IsSidebarVisible;
        Settings.SidebarWidth = live.SidebarWidth;
        Settings.FontScale = live.FontScale;
        Settings.WindowWidth = live.WindowWidth;
        Settings.WindowHeight = live.WindowHeight;
        Settings.WindowLeft = live.WindowLeft;
        Settings.WindowTop = live.WindowTop;
        Settings.WindowMaximized = live.WindowMaximized;
        Settings.LastSessionId = live.LastSessionId;
    }

    public string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AvaloniaAssistant");

    [ObservableProperty]
    private string savedText = string.Empty;

    [ObservableProperty]
    private bool isSaving;

    [ObservableProperty]
    private string saveButtonText = "保存设置";

    [ObservableProperty]
    private bool showSaveToast;

    [ObservableProperty]
    private string saveToastText = "设置已保存";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SaveToastBrush))]
    [NotifyPropertyChangedFor(nameof(SaveToastIcon))]
    private bool isSaveError;

    public IBrush SaveToastBrush => IsSaveError
        ? new SolidColorBrush(Color.Parse("#B3261E"))
        : new SolidColorBrush(Color.Parse("#2E7D32"));

    public Geometry SaveToastIcon => IsSaveError ? ArcIcons.CircleAlert : ArcIcons.CircleCheck;

    /// <summary>「测试连接」正在进行。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestButtonText))]
    [NotifyPropertyChangedFor(nameof(TestBrush))]
    private bool isTesting;

    public string TestButtonText => IsTesting ? "测试中…" : "测试连接";

    /// <summary>测试结果。改任何一项设置都会清掉，免得旧的「连上了」挂在改过的表单上。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTestResult))]
    [NotifyPropertyChangedFor(nameof(TestBrush))]
    private string? testResultText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TestBrush))]
    private bool testOk;

    public bool ShowTestResult => !string.IsNullOrWhiteSpace(TestResultText);

    public IBrush TestBrush => IsTesting
        ? new SolidColorBrush(Color.Parse("#6F6F69"))
        : TestOk
            ? new SolidColorBrush(Color.Parse("#2E7D32"))
            : new SolidColorBrush(Color.Parse("#B3261E"));

    /// <summary>用表单里这份（还没保存的）设置发一次最小请求，别让人保存完再靠发消息去试。</summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (IsTesting || IsSaving)
        {
            return;
        }

        IsTesting = true;
        TestOk = true;
        TestResultText = "正在请求…";

        try
        {
            var result = await _host.Runtime.TestConnectionAsync(Settings);
            TestOk = result.Ok;
            TestResultText = result.Summary;
        }
        catch (Exception ex)
        {
            TestOk = false;
            TestResultText = "测试失败：" + ex.Message;
        }
        finally
        {
            IsTesting = false;
        }
    }

    private DispatcherTimer? _toastTimer;

    [RelayCommand]
    private void SelectProvider(ProviderOptionViewModel option)
    {
        Settings.Provider = option.Preset.Kind;
        Settings.BaseUrl = option.Preset.BaseUrl;
        Settings.Model = option.Preset.Model;
        // 换服务商时协议回到推荐值，用户想用代理绕协议可以再手动改。
        Settings.Protocol = option.Preset.Protocol;

        foreach (var provider in Providers)
        {
            provider.IsSelected = ReferenceEquals(provider, option);
        }

        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(Model));
        NotifyProtocolChanged();
        Touch();
        ClearTestResult();
    }

    [RelayCommand]
    private void SetProtocol(string protocol)
    {
        Settings.Protocol = protocol switch
        {
            "responses" => AgentProtocol.OpenAiResponses,
            "messages" => AgentProtocol.AnthropicMessages,
            _ => AgentProtocol.OpenAiCompatible,
        };

        NotifyProtocolChanged();
        Touch();
        ClearTestResult();
    }

    private void NotifyProtocolChanged()
    {
        OnPropertyChanged(nameof(IsChatProtocolSelected));
        OnPropertyChanged(nameof(IsResponsesProtocolSelected));
        OnPropertyChanged(nameof(IsMessagesProtocolSelected));
        OnPropertyChanged(nameof(ProtocolHint));
    }

    [RelayCommand]
    private void SetPermission(string mode)
    {
        Settings.Permission = mode switch
        {
            "readonly" => PermissionMode.ReadOnly,
            "full" => PermissionMode.FullAccess,
            _ => PermissionMode.Workspace,
        };

        OnPropertyChanged(nameof(IsReadOnlySelected));
        OnPropertyChanged(nameof(IsWorkspaceSelected));
        OnPropertyChanged(nameof(IsFullAccessSelected));
        Touch();
    }

    [RelayCommand]
    private async Task BrowseWorkspaceAsync()
    {
        var folder = await _host.Picker.PickFolderAsync("选择工作区");
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        Settings.WorkspacePath = folder;
        Settings.RecentWorkspaces.RemoveAll(path => string.Equals(path, folder, StringComparison.OrdinalIgnoreCase));
        Settings.RecentWorkspaces.Insert(0, folder);
        OnPropertyChanged(nameof(WorkspacePath));
        Touch();
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        IsSaving = true;
        SaveButtonText = "保存中…";
        try
        {
            // 窗口/侧栏/最后会话这些是后台实时写的，不能被进页面时的快照覆盖回去。
            SyncWindowPreferences();

            if (string.IsNullOrWhiteSpace(Settings.Model))
            {
                Settings.Model = ProviderPreset.For(Settings.Provider).Model;
            }

            if (string.IsNullOrWhiteSpace(Settings.BaseUrl))
            {
                Settings.BaseUrl = ProviderPreset.For(Settings.Provider).BaseUrl;
            }

            await _host.SettingsProvider.SaveAsync(Settings);
            _baseline = Settings.Clone();
            ShowLeavePrompt = false;
            Touch();
            SavedText = $"已保存 · {DateTime.Now:HH:mm:ss}";
            ShowToast("设置已保存", error: false);
        }
        catch (Exception ex)
        {
            SavedText = "保存失败：" + ex.Message;
            ShowToast("保存失败，请重试", error: true);
        }
        finally
        {
            IsSaving = false;
        }
    }

    private void ShowToast(string text, bool error)
    {
        SaveToastText = text;
        IsSaveError = error;
        ShowSaveToast = true;
        SaveButtonText = error ? "重新保存" : "已保存";

        _toastTimer?.Stop();
        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.6) };
        _toastTimer.Tick += (_, _) =>
        {
            ShowSaveToast = false;
            SaveButtonText = "保存设置";
            _toastTimer?.Stop();
            _toastTimer = null;
        };
        _toastTimer.Start();
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(DataFolder);
        Services.Ui.OpenExternal(DataFolder);
    }

    /// <summary>改过东西就直接返回，等于把改动丢掉；先问一句，给「保存并返回」和「放弃」两条路。</summary>
    [RelayCommand]
    private void Back()
    {
        if (HasUnsavedChanges)
        {
            ShowLeavePrompt = true;
            return;
        }

        _host.OpenHome();
    }

    [RelayCommand]
    private async Task SaveAndLeaveAsync()
    {
        await SaveAsync();
        if (!IsSaveError)
        {
            _host.OpenHome();
        }
    }

    [RelayCommand]
    private void DiscardAndLeave() => _host.OpenHome();
}
