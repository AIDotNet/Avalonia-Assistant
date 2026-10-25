using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
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

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _host;
    private string _savedSnapshot;

    public SettingsViewModel(MainWindowViewModel host)
    {
        _host = host;
        Settings = host.Settings.Clone();
        _savedSnapshot = Snapshot(Settings);

        Providers = new ObservableCollection<ProviderOptionViewModel>(
            ProviderPreset.All.Select(preset => new ProviderOptionViewModel(preset, SelectProvider)
            {
                IsSelected = preset.Kind == Settings.Provider,
            }));

        SavedText = string.Empty;
    }

    public AssistantSettings Settings { get; }

    public ObservableCollection<ProviderOptionViewModel> Providers { get; }

    /// <summary>表单里有没有还没保存的改动。设置页改的是副本，直接走人不会提示，等于白填。</summary>
    public bool IsDirty => !string.Equals(Snapshot(Settings), _savedSnapshot, StringComparison.Ordinal);

    private static string Snapshot(AssistantSettings settings) => JsonSerializer.Serialize(settings, AssistantJson.Options);

    public string ApiKey
    {
        get => Settings.ApiKey;
        set
        {
            Settings.ApiKey = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDirty));
            ResetProbe();
        }
    }

    public string BaseUrl
    {
        get => Settings.BaseUrl;
        set
        {
            Settings.BaseUrl = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDirty));
            ResetProbe();
        }
    }

    public string Model
    {
        get => Settings.Model;
        set
        {
            Settings.Model = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDirty));
            ResetProbe();
        }
    }

    public double Temperature
    {
        get => Settings.Temperature;
        set
        {
            Settings.Temperature = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public int MaxTokens
    {
        get => Settings.MaxTokens;
        set
        {
            Settings.MaxTokens = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    public string WorkspacePath => Settings.WorkspacePath;

    public bool IsReadOnlySelected => Settings.Permission == PermissionMode.ReadOnly;

    public bool IsWorkspaceSelected => Settings.Permission == PermissionMode.Workspace;

    public bool IsFullAccessSelected => Settings.Permission == PermissionMode.FullAccess;

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

    /// <summary>测试连接的结论。失败时直接把请求的 URL 和状态码写出来，方便判断是 Key 错还是地址错。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProbeResult))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private string? probeText;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private bool probeFailed;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ProbeCommand))]
    [NotifyPropertyChangedFor(nameof(ProbeBrush))]
    private bool isProbing;

    public bool HasProbeResult => !string.IsNullOrWhiteSpace(ProbeText);

    public string ProbeButtonText => IsProbing ? "正在测试…" : "测试连接";

    public IBrush ProbeBrush => IsProbing
        ? new SolidColorBrush(Color.Parse("#6F6F69"))
        : ProbeFailed
            ? new SolidColorBrush(Color.Parse("#B3261E"))
            : new SolidColorBrush(Color.Parse("#2E7D32"));

    private DispatcherTimer? _toastTimer;

    /// <summary>改了地址/密钥/模型，上一次的测试结论就过期了，留着会误导。</summary>
    private void ResetProbe()
    {
        if (!IsProbing && HasProbeResult)
        {
            ProbeText = null;
            ProbeFailed = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanProbe))]
    private async Task ProbeAsync()
    {
        IsProbing = true;
        ProbeFailed = false;
        ProbeText = "正在连接…";

        try
        {
            var result = await _host.Runtime.ProbeAsync(Settings);
            if (!result.Ok)
            {
                ProbeFailed = true;
                ProbeText = result.Message;
                return;
            }

            if (result.Models.Count > 0 && !result.Models.Contains(Settings.Model, StringComparer.Ordinal))
            {
                ProbeFailed = true;
                ProbeText = $"能连上，但服务端没有「{Settings.Model}」这个模型。可用："
                            + string.Join("、", result.Models.Take(5))
                            + (result.Models.Count > 5 ? " …" : string.Empty);
                return;
            }

            ProbeText = result.Message;
        }
        catch (Exception ex)
        {
            ProbeFailed = true;
            ProbeText = "测试失败：" + ex.Message;
        }
        finally
        {
            IsProbing = false;
        }
    }

    private bool CanProbe() => !IsProbing;

    [RelayCommand]
    private void SelectProvider(ProviderOptionViewModel option)
    {
        Settings.Provider = option.Preset.Kind;
        Settings.BaseUrl = option.Preset.BaseUrl;
        Settings.Model = option.Preset.Model;

        foreach (var provider in Providers)
        {
            provider.IsSelected = ReferenceEquals(provider, option);
        }

        OnPropertyChanged(nameof(BaseUrl));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(IsDirty));
        ResetProbe();
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
        OnPropertyChanged(nameof(IsDirty));
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
        OnPropertyChanged(nameof(IsDirty));
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
            if (string.IsNullOrWhiteSpace(Settings.Model))
            {
                Settings.Model = ProviderPreset.For(Settings.Provider).Model;
            }

            if (string.IsNullOrWhiteSpace(Settings.BaseUrl))
            {
                Settings.BaseUrl = ProviderPreset.For(Settings.Provider).BaseUrl;
            }

            await _host.SettingsProvider.SaveAsync(Settings);
            _savedSnapshot = Snapshot(Settings);
            OnPropertyChanged(nameof(IsDirty));
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
        if (!Services.Ui.OpenExternal(DataFolder))
        {
            SavedText = "打不开数据目录，可能被系统策略拦住了。";
            ShowToast("打不开数据目录", error: true);
        }
    }

    [RelayCommand]
    private void Back() => _host.OpenHome();
}
