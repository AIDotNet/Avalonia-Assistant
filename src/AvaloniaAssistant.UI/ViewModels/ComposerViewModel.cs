using System.Collections.ObjectModel;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed partial class ComposerViewModel : ObservableObject
{
    private readonly IAssistantRuntime _runtime;
    private readonly ISettingsProvider _settings;
    private readonly IFilePicker _picker;
    private readonly Func<Task> _openSettings;

    public ComposerViewModel(
        IAssistantRuntime runtime,
        ISettingsProvider settings,
        IFilePicker picker,
        Func<Task> openSettings)
    {
        _runtime = runtime;
        _settings = settings;
        _picker = picker;
        _openSettings = openSettings;
        RefreshModels();
        _settings.Changed += (_, _) => Refresh();
    }

    public event EventHandler<string>? SendRequested;

    public event EventHandler? StopRequested;

    public ObservableCollection<ModelOption> Models { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private string? text;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyPropertyChangedFor(nameof(ShowSend))]
    [NotifyPropertyChangedFor(nameof(ShowStop))]
    private bool isBusy;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);

    public bool ShowSend => !IsBusy;

    public bool ShowStop => IsBusy;

    public string Placeholder => "描述你要做的事，Enter 发送，Shift+Enter 换行";

    public string ModelLabel => _settings.Current.Model;

    public string PermissionLabel => PermissionModeText.Describe(_settings.Current.Permission);

    public string WorkspaceLabel => ShortWorkspace(_settings.Current.WorkspacePath);

    public string WorkspacePath => _settings.Current.WorkspacePath;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_settings.Current.ApiKey);

    public IReadOnlyList<string> RecentWorkspaces => _settings.Current.RecentWorkspaces;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private void Send()
    {
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

    private bool CanSend() => !IsBusy && !string.IsNullOrWhiteSpace(Text);

    [RelayCommand]
    private void Stop() => StopRequested?.Invoke(this, EventArgs.Empty);

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
        _settings.Current.Permission = permission;
        Refresh();
        await _settings.SaveAsync(_settings.Current);
    }

    public void Refresh()
    {
        RefreshModels();
        OnPropertyChanged(nameof(ModelLabel));
        OnPropertyChanged(nameof(PermissionLabel));
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
            return "选择工作区";
        }

        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
