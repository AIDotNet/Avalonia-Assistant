using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
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

/// <summary>
/// 设置页里的一套模型配置。字段直接读写 <see cref="ModelProfile"/>，
/// 每次改动都回叫设置页，用来重算「有改动」和列表里的那两行摘要。
/// </summary>
public sealed partial class ModelProfileViewModel : ObservableObject
{
    private readonly Action<ModelProfileViewModel> _select;
    private readonly Action _changed;
    private string _contextWindowText;

    public ModelProfileViewModel(ModelProfile profile, Action<ModelProfileViewModel> select, Action changed)
    {
        Profile = profile;
        _select = select;
        _changed = changed;
        _contextWindowText = profile.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    }

    public ModelProfile Profile { get; }

    public string Id => Profile.Id;

    public string Name
    {
        get => Profile.Name;
        set
        {
            var text = value ?? string.Empty;
            if (string.Equals(Profile.Name, text, StringComparison.Ordinal))
            {
                return;
            }

            Profile.Name = text;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            Touch();
        }
    }

    public string DisplayName => Profile.DisplayName;

    /// <summary>列表里第二行：服务商 · 协议 · 主机。三样都对得上才分得清两个同名模型。</summary>
    public string Summary
        => string.Join(
            " · ",
            new[]
            {
                Profile.ProviderName,
                Profile.EffectiveProtocol switch
                {
                    AgentProtocol.AnthropicMessages => "Messages",
                    AgentProtocol.OpenAiResponses => "Responses",
                    _ => "Chat",
                },
                EndpointHost,
            }.Where(part => !string.IsNullOrWhiteSpace(part)));

    public string EndpointHint => string.IsNullOrWhiteSpace(Profile.BaseUrl) ? "还没填 Base URL" : Profile.BaseUrl;

    public string BaseUrl
    {
        get => Profile.BaseUrl;
        set
        {
            if (string.Equals(Profile.BaseUrl, value, StringComparison.Ordinal))
            {
                return;
            }

            Profile.BaseUrl = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(EndpointHint));
            OnPropertyChanged(nameof(Summary));
            Touch();
        }
    }

    public string ApiKey
    {
        get => Profile.ApiKey;
        set
        {
            if (string.Equals(Profile.ApiKey, value, StringComparison.Ordinal))
            {
                return;
            }

            Profile.ApiKey = value ?? string.Empty;
            OnPropertyChanged();
            Touch();
        }
    }

    public string ModelId
    {
        get => Profile.Model;
        set
        {
            var text = value ?? string.Empty;
            if (string.Equals(Profile.Model, text, StringComparison.Ordinal))
            {
                return;
            }

            Profile.Model = text;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayName));
            Touch();
        }
    }

    /// <summary>
    /// 上下文窗口覆盖值。留空＝按本地模型目录查；填了就必须是正整数，
    /// 填错时保存会被拦下来，而不是悄悄按「没填」处理。
    /// </summary>
    public string ContextWindowText
    {
        get => _contextWindowText;
        set
        {
            if (!SetProperty(ref _contextWindowText, value ?? string.Empty))
            {
                return;
            }

            ValidateContextWindow(_contextWindowText);
            Touch();
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextWindowError))]
    private string? contextWindowError;

    public bool HasContextWindowError => !string.IsNullOrWhiteSpace(ContextWindowError);

    public string ContextWindowHint => Profile.ContextWindow is { } window
        ? $"按 {window / 1000d:0.#}k 计算水位与压缩阈值"
        : "留空则按本地模型目录估算";

    private void ValidateContextWindow(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
        {
            ContextWindowError = null;
            Profile.ContextWindow = null;
            OnPropertyChanged(nameof(ContextWindowHint));
            return;
        }

        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) || value <= 0)
        {
            ContextWindowError = "填正整数，例如 128000；不确定就留空";
            OnPropertyChanged(nameof(ContextWindowHint));
            return;
        }

        ContextWindowError = null;
        Profile.ContextWindow = value;
        OnPropertyChanged(nameof(ContextWindowHint));
    }

    public bool IsChatProtocolSelected => Profile.EffectiveProtocol == AgentProtocol.OpenAiCompatible;

    public bool IsResponsesProtocolSelected => Profile.EffectiveProtocol == AgentProtocol.OpenAiResponses;

    public bool IsMessagesProtocolSelected => Profile.EffectiveProtocol == AgentProtocol.AnthropicMessages;

    /// <summary>
    /// 协议这一行的标签。没手动选过就直说「跟随预设」——
    /// 以前把这个状态塞在说明文字末尾，窄一点就折成孤零零一行「荐）」。
    /// </summary>
    public string ProtocolLabel => Profile.Protocol is null ? "协议 · 跟随预设" : "协议";

    public string ProtocolHint => ProtocolCatalog.For(Profile.EffectiveProtocol).Description;

    [ObservableProperty]
    private bool isSelected;

    /// <summary>是不是输入框那里正在用的那一套。</summary>
    [ObservableProperty]
    private bool isActive;

    [ObservableProperty]
    private bool canDelete;

    [RelayCommand]
    private void Select() => _select(this);

    [RelayCommand]
    private void SetProtocol(string protocol)
    {
        Profile.Protocol = protocol switch
        {
            "responses" => AgentProtocol.OpenAiResponses,
            "messages" => AgentProtocol.AnthropicMessages,
            _ => AgentProtocol.OpenAiCompatible,
        };

        NotifyProtocolChanged();
        Touch();
    }

    /// <summary>服务商只是预设来源：换它时把地址、模型、协议一起换成该预设的默认值。</summary>
    public void ApplyPreset(ProviderPreset preset)
    {
        Profile.Provider = preset.Kind;
        Profile.BaseUrl = preset.BaseUrl;
        Profile.Model = preset.Model;
        Profile.Protocol = preset.Protocol;

        _contextWindowText = Profile.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
        OnPropertyChanged(nameof(ContextWindowText));
        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(ModelId));
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(EndpointHint));
        NotifyProtocolChanged();
        Touch();
    }

    public void RefreshProtocolSummary()
    {
        NotifyProtocolChanged();
        OnPropertyChanged(nameof(Summary));
    }

    private void NotifyProtocolChanged()
    {
        OnPropertyChanged(nameof(IsChatProtocolSelected));
        OnPropertyChanged(nameof(IsResponsesProtocolSelected));
        OnPropertyChanged(nameof(IsMessagesProtocolSelected));
        OnPropertyChanged(nameof(ProtocolLabel));
        OnPropertyChanged(nameof(ProtocolHint));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(ContextWindowHint));
    }

    private string EndpointHost
        => Uri.TryCreate(Profile.BaseUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

    private void Touch() => _changed();
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

        // 直接写字段：走属性会触发校验和「有改动」，刚进页面就变成脏的
        _temperatureText = Settings.Temperature.ToString("0.##", CultureInfo.InvariantCulture);
        _maxTokensText = Settings.MaxTokens.ToString(CultureInfo.InvariantCulture);

        // Ctrl+滚轮在设置页也能用：档位高亮要跟着实际缩放走，不能停在进页面时的快照上。
        _host.FontScaleChanged += OnHostFontScaleChanged;

        Providers = new ObservableCollection<ProviderOptionViewModel>(
            ProviderPreset.All.Select(preset => new ProviderOptionViewModel(preset, SelectProvider)));

        LoadProfileList();

        SavedText = string.Empty;
    }

    public void Dispose() => _host.FontScaleChanged -= OnHostFontScaleChanged;

    private void OnHostFontScaleChanged(object? sender, EventArgs e)
    {
        Settings.FontScale = _host.FontScale;
        NotifyScaleChanged();
    }

    public AssistantSettings Settings { get; }

    /// <summary>
    /// 当前看的是哪一栏：<c>system</c> 系统设置，<c>models</c> 模型设置。
    /// 两栏只是同一份设置的两种视角，切换不丢改动——保存条一直在底部。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsModelSection))]
    [NotifyPropertyChangedFor(nameof(IsSystemSection))]
    private string selectedSection = "models";

    /// <summary>只有明确选了 system 才显示系统设置：绑定还没落值时先按模型设置渲染，不会两栏同时出现。</summary>
    public bool IsSystemSection => string.Equals(SelectedSection, "system", StringComparison.OrdinalIgnoreCase);

    public bool IsModelSection => !IsSystemSection;

    /// <summary>配好的模型。列表本身是可绑定的，删改都直接反映到 <see cref="AssistantSettings.Models"/>。</summary>
    public ObservableCollection<ModelProfileViewModel> Profiles { get; } = [];

    public ObservableCollection<ProviderOptionViewModel> Providers { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedProfile))]
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    [NotifyPropertyChangedFor(nameof(CanDeleteProfile))]
    private ModelProfileViewModel? selectedProfile;

    public bool HasSelectedProfile => SelectedProfile is not null;

    public bool CanDeleteProfile => Profiles.Count > 1;

    partial void OnSelectedProfileChanged(ModelProfileViewModel? value)
    {
        foreach (var profile in Profiles)
        {
            profile.IsSelected = ReferenceEquals(profile, value);
        }

        RefreshProviderSelection();
        ClearTestResult();
    }

    private void LoadProfileList()
    {
        Profiles.Clear();
        foreach (var profile in Settings.Models)
        {
            Profiles.Add(CreateProfileViewModel(profile));
        }

        RefreshDeleteAvailability();
        SelectedProfile = Profiles.FirstOrDefault(item => item.Id == Settings.ActiveModelId) ?? Profiles.FirstOrDefault();
        RefreshActiveFlags();
        RefreshProviderSelection();
    }

    private ModelProfileViewModel CreateProfileViewModel(ModelProfile profile)
        => new(profile, SelectProfile, Touch) { CanDelete = Profiles.Count > 1 };

    private void SelectProfile(ModelProfileViewModel profile) => SelectedProfile = profile;

    /// <summary>只改「当前用哪套」：真正的生效要等保存，和这一页的其他设置保持一致。</summary>
    [RelayCommand]
    private void UseSelectedProfile()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        Settings.ActiveModelId = SelectedProfile.Id;
        RefreshActiveFlags();
        Touch();
    }

    [RelayCommand]
    private void AddProfile()
    {
        var created = ModelProfile.Create(AgentProviderKind.Custom, usePresetDefaults: true);
        Settings.Models.Add(created);

        var item = CreateProfileViewModel(created);
        Profiles.Add(item);
        RefreshDeleteAvailability();
        SelectedProfile = item;

        // 只有一套时它就是当前模型，新建的这套不必再点一次「设为当前」
        if (Profiles.Count == 1 || string.IsNullOrWhiteSpace(Settings.ActiveModelId))
        {
            Settings.ActiveModelId = created.Id;
            RefreshActiveFlags();
        }

        Touch();
    }

    [RelayCommand]
    private void DeleteSelectedProfile()
    {
        if (SelectedProfile is not { } item || Profiles.Count <= 1)
        {
            return;
        }

        var index = Profiles.IndexOf(item);
        Settings.Models.Remove(item.Profile);
        Profiles.Remove(item);

        // 删掉的正是当前用的那套：接到列表里剩下的第一套，别让「当前模型」指向一个不存在的 id
        if (string.Equals(Settings.ActiveModelId, item.Id, StringComparison.Ordinal))
        {
            Settings.ActiveModelId = Profiles[0].Id;
        }

        RefreshActiveFlags();
        RefreshDeleteAvailability();
        SelectedProfile = Profiles[Math.Clamp(index, 0, Profiles.Count - 1)];
        Touch();
    }

    private void RefreshActiveFlags()
    {
        foreach (var profile in Profiles)
        {
            profile.IsActive = string.Equals(profile.Id, Settings.ActiveModelId, StringComparison.Ordinal);
        }
    }

    private void RefreshDeleteAvailability()
    {
        var canDelete = Profiles.Count > 1;
        foreach (var profile in Profiles)
        {
            profile.CanDelete = canDelete;
        }

        OnPropertyChanged(nameof(CanDeleteProfile));
    }

    private void RefreshProviderSelection()
    {
        var kind = SelectedProfile?.Profile.Provider;
        foreach (var provider in Providers)
        {
            provider.IsSelected = kind is not null && provider.Preset.Kind == kind;
        }
    }

    public string DataFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AvaloniaAssistant");

    /// <summary>
    /// 数值字段用字符串接：以前直接绑 double / int，填错时绑定静默失败，
    /// 字段里留着错的值，点保存照样弹「已保存」——用户以为存进去了。
    /// </summary>
    private string _temperatureText = string.Empty;

    public string TemperatureText
    {
        get => _temperatureText;
        set
        {
            if (!SetProperty(ref _temperatureText, value))
            {
                return;
            }

            ValidateTemperature(value);
        }
    }

    private string _maxTokensText = string.Empty;

    public string MaxTokensText
    {
        get => _maxTokensText;
        set
        {
            if (!SetProperty(ref _maxTokensText, value))
            {
                return;
            }

            ValidateMaxTokens(value);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTemperatureError))]
    [NotifyPropertyChangedFor(nameof(HasFieldErrors))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string? temperatureError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMaxTokensError))]
    [NotifyPropertyChangedFor(nameof(HasFieldErrors))]
    [NotifyPropertyChangedFor(nameof(CanSave))]
    private string? maxTokensError;

    public bool HasTemperatureError => !string.IsNullOrWhiteSpace(TemperatureError);

    public bool HasMaxTokensError => !string.IsNullOrWhiteSpace(MaxTokensError);

    /// <summary>有任一字段没填对：保存要拦下来，不能假装成功。</summary>
    public bool HasFieldErrors => HasTemperatureError || HasMaxTokensError || HasProfileErrors;

    /// <summary>模型里也有必填项（地址、模型名）和上下文窗口的数字校验。</summary>
    private bool HasProfileErrors => Profiles.Any(profile => profile.HasContextWindowError);

    /// <summary>
    /// 保存按钮能不能点。字段没填对时直接禁掉——点得下去、点了才说「没保存」，
    /// 等于让用户白按一次；验收口径写的是「非法时保存按钮不可用」。
    /// </summary>
    public bool CanSave => !IsSaving && !HasFieldErrors;

    private void ValidateTemperature(string? text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            || value < 0
            || value > 2)
        {
            TemperatureError = "填 0 到 2 之间的数，例如 0.7";
            return;
        }

        TemperatureError = null;
        if (Math.Abs(Settings.Temperature - value) > 0.0001)
        {
            Settings.Temperature = value;
            Touch();
        }
    }

    private void ValidateMaxTokens(string? text)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            || value < 1
            || value > 1_000_000)
        {
            MaxTokensError = "填 1 到 1000000 之间的整数";
            return;
        }

        MaxTokensError = null;
        if (Settings.MaxTokens != value)
        {
            Settings.MaxTokens = value;
            Touch();
        }
    }

    /// <summary>有没有还没保存的改动。返回按钮和底部提示都看它。</summary>
    public bool HasUnsavedChanges =>
        Settings.Permission != _baseline.Permission
        || Settings.MaxTokens != _baseline.MaxTokens
        || Math.Abs(Settings.Temperature - _baseline.Temperature) > 0.0001
        || !string.Equals(Settings.WorkspacePath, _baseline.WorkspacePath, StringComparison.OrdinalIgnoreCase)
        || ModelsChanged();

    /// <summary>
    /// 模型列表是不是改过。逐字段比：增删一套、改个地址、换个协议、连删两套又加回来，
    /// 只有完全一样才算没动。
    /// </summary>
    private bool ModelsChanged()
    {
        if (!string.Equals(Settings.ActiveModelId, _baseline.ActiveModelId, StringComparison.Ordinal))
        {
            return true;
        }

        if (Settings.Models.Count != _baseline.Models.Count)
        {
            return true;
        }

        for (var index = 0; index < Settings.Models.Count; index++)
        {
            if (!Settings.Models[index].SameAs(_baseline.Models[index]))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>点了返回但还有改动：底部换成「保存并返回 / 放弃改动」，别一声不响地丢掉。</summary>
    [ObservableProperty]
    private bool showLeavePrompt;

    private void Touch()
    {
        OnPropertyChanged(nameof(HasUnsavedChanges));
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(HasFieldErrors));
        ClearTestResult();
    }

    /// <summary>改了和连接有关的字段，上一次的测试结论就不作数了。</summary>
    private void ClearTestResult()
    {
        if (TestResultText is not null)
        {
            TestResultText = null;
        }
    }

    public string WorkspacePath => Settings.WorkspacePath;

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

    /// <summary>「关于」卡片里的版本行。和侧栏同一个来源，不会再出现两处对不上。</summary>
    public string AppVersionText => "Avalonia Assistant " + AppVersion.Number;

    public bool IsSoundOn => Settings.PlayFinishSound;

    public bool IsSoundOff => !Settings.PlayFinishSound;

    /// <summary>
    /// 跑完的提示音。有人开着会议软件、有人在图书馆，这声音不是所有人都想要，
    /// 所以给个开关；任务栏闪烁不受影响，那是视觉提示。
    /// </summary>
    [RelayCommand]
    private async Task SetSoundAsync(string value)
    {
        var on = !string.Equals(value, "off", StringComparison.OrdinalIgnoreCase);
        Settings.PlayFinishSound = on;
        _baseline.PlayFinishSound = on;
        _host.Settings.PlayFinishSound = on;
        OnPropertyChanged(nameof(IsSoundOn));
        OnPropertyChanged(nameof(IsSoundOff));
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

    [ObservableProperty]
    private string savedText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSave))]
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
    [NotifyPropertyChangedFor(nameof(CanTestConnection))]
    private bool isTesting;

    public string TestButtonText => IsTesting ? "测试中…" : "测试连接";

    public bool CanTestConnection => !IsTesting && !IsSaving && HasSelectedProfile;

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
        ? new SolidColorBrush(Color.FromArgb(0x99, 0x6F, 0x6F, 0x69))
        : TestOk
            ? new SolidColorBrush(Color.Parse("#2E7D32"))
            : new SolidColorBrush(Color.Parse("#B3261E"));

    /// <summary>
    /// 用选中的那一套（还没保存的也可以）发一次最小请求。
    /// 以前只能测「当前配置」，现在列表里改哪套就测哪套。
    /// </summary>
    [RelayCommand]
    private async Task TestConnectionAsync()
    {
        if (!CanTestConnection || SelectedProfile is null)
        {
            return;
        }

        IsTesting = true;
        TestOk = true;
        TestResultText = "正在请求…";

        try
        {
            var probe = new AssistantSettings();
            probe.Models = [SelectedProfile.Profile.Clone()];
            probe.ActiveModelId = SelectedProfile.Id;

            var result = await _host.Runtime.TestConnectionAsync(probe);
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
        if (SelectedProfile is null)
        {
            return;
        }

        SelectedProfile.ApplyPreset(option.Preset);
        RefreshProviderSelection();
        ClearTestResult();
        Touch();
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
    private async Task SaveAsync() => await TrySaveAsync();

    /// <summary>
    /// 真正落盘。返回 false 表示没存（字段没填对 / 写盘失败），
    /// 「保存并返回」要靠这个返回值决定要不要真的离开页面。
    /// </summary>
    private async Task<bool> TrySaveAsync()
    {
        if (IsSaving)
        {
            return false;
        }

        if (HasFieldErrors)
        {
            // 错在模型那栏却停在基础设置：跳回去，否则用户只看到保存按钮是灰的，找不到哪项没填对
            if (HasProfileErrors)
            {
                SelectedSection = "models";
            }

            // 字段里有错就别提示「已保存」：用户会以为那份错的配置生效了
            SavedText = "有参数没填对，先按提示改一下";
            ShowToast("有参数没填对，还没保存", error: true);
            return false;
        }

        IsSaving = true;
        SaveButtonText = "保存中…";
        try
        {
            // 窗口/侧栏/最后会话这些是后台实时写的，不能被进页面时的快照覆盖回去。
            SyncWindowPreferences();
            Settings.NormalizeModels();

            await _host.SettingsProvider.SaveAsync(Settings);
            _baseline = Settings.Clone();
            ShowLeavePrompt = false;

            // 归一化可能补了空模型名/空地址：重建一次列表，界面显示的才是真正存进去的那份
            var keep = SelectedProfile?.Id;
            LoadProfileList();
            SelectedProfile = Profiles.FirstOrDefault(item => item.Id == keep) ?? SelectedProfile;
            Touch();
            SavedText = $"已保存 · {DateTime.Now:HH:mm:ss}";
            ShowToast("设置已保存", error: false);
            return true;
        }
        catch (Exception ex)
        {
            SavedText = "保存失败：" + ex.Message;
            ShowToast("保存失败，请重试", error: true);
            return false;
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
        try
        {
            Directory.CreateDirectory(DataFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowToast("数据目录不可用：" + ex.Message, error: true);
            return;
        }

        if (!Services.Ui.OpenExternal(DataFolder))
        {
            ShowToast("打不开数据目录，可能被系统策略拦住了。", error: true);
        }
    }

    /// <summary>返回按钮的悬停提示：有来源任务时写清回到哪，别让人猜「返回」是回首页还是回对话。</summary>
    public string BackHint => _host.ReturnHint;

    /// <summary>页头那条面包屑显不显示：有「刚才那条任务」才显示。</summary>
    public bool HasReturnTarget => _host.HasReturnTarget;

    /// <summary>改过东西就直接返回，等于把改动丢掉；先问一句，给「保存并返回」和「放弃」两条路。</summary>
    [RelayCommand]
    private void Back()
    {
        if (HasUnsavedChanges)
        {
            ShowLeavePrompt = true;
            return;
        }

        _host.GoBack();
    }

    [RelayCommand]
    private async Task SaveAndLeaveAsync()
    {
        // 字段没填对时不要「保存失败也照样走人」——那样改动就静默丢了
        if (await TrySaveAsync())
        {
            _host.GoBack();
        }
    }

    [RelayCommand]
    private void DiscardAndLeave() => _host.GoBack();
}
