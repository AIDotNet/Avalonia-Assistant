using System.Collections.ObjectModel;
using AvaloniaAssistant.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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

    public SettingsViewModel(MainWindowViewModel host)
    {
        _host = host;
        Settings = host.Settings.Clone();

        Providers = new ObservableCollection<ProviderOptionViewModel>(
            ProviderPreset.All.Select(preset => new ProviderOptionViewModel(preset, SelectProvider)
            {
                IsSelected = preset.Kind == Settings.Provider,
            }));

        SavedText = string.Empty;
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
        }
    }

    public string BaseUrl
    {
        get => Settings.BaseUrl;
        set
        {
            Settings.BaseUrl = value;
            OnPropertyChanged();
        }
    }

    public string Model
    {
        get => Settings.Model;
        set
        {
            Settings.Model = value;
            OnPropertyChanged();
        }
    }

    public double Temperature
    {
        get => Settings.Temperature;
        set
        {
            Settings.Temperature = value;
            OnPropertyChanged();
        }
    }

    public int MaxTokens
    {
        get => Settings.MaxTokens;
        set
        {
            Settings.MaxTokens = value;
            OnPropertyChanged();
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
    private string savedText;

    [ObservableProperty]
    private bool isSaving;

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
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (IsSaving)
        {
            return;
        }

        IsSaving = true;
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
            SavedText = $"已保存 · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            SavedText = "保存失败：" + ex.Message;
        }
        finally
        {
            IsSaving = false;
        }
    }

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(DataFolder);
        Services.Ui.OpenExternal(DataFolder);
    }

    [RelayCommand]
    private void Back() => _host.OpenHome();
}
