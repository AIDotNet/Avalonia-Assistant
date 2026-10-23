using System.Collections.ObjectModel;
using AvaloniaAssistant.Abstractions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public sealed class ToolGroupViewModel
{
    public ToolGroupViewModel(string name, IEnumerable<ToolItemViewModel> items)
    {
        Name = name;
        Items = new ObservableCollection<ToolItemViewModel>(items);
    }

    public string Name { get; }

    public ObservableCollection<ToolItemViewModel> Items { get; }
}

public sealed partial class ToolItemViewModel : ObservableObject
{
    private readonly ToolToggle _toggle;
    private bool _initializing = true;

    public ToolItemViewModel(AgentToolInfo info, bool enabled, ToolToggle toggle)
    {
        Info = info;
        _toggle = toggle;
        IsEnabled = enabled;
        _initializing = false;
    }

    public AgentToolInfo Info { get; }

    public string Name => Info.Name;

    public string Label => Info.Label;

    public string Description => Info.Description;

    public string Category => Info.Category;

    public string ApprovalText => Info.RequiresApproval ? "需要确认" : "自动执行";

    public string MutatingText => Info.IsMutating ? "会修改本机" : "只读";

    public string ParameterText => Info.Parameters.Count == 0
        ? "无参数"
        : string.Join(" · ", Info.Parameters.Select(parameter =>
            $"{parameter.Name}:{parameter.Type}{(parameter.Required ? "*" : string.Empty)}"));

    [ObservableProperty]
    private bool isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_initializing)
        {
            _toggle(Name, value);
        }
    }
}

public delegate void ToolToggle(string name, bool enabled);

public sealed partial class ToolsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _host;

    public ToolsViewModel(MainWindowViewModel host)
    {
        _host = host;
        var disabled = host.Settings.DisabledTools;

        Groups = new ObservableCollection<ToolGroupViewModel>(
            host.Runtime.Tools
                .GroupBy(tool => tool.Category)
                .Select(group => new ToolGroupViewModel(
                    group.Key,
                    group.Select(tool => new ToolItemViewModel(tool, !disabled.Contains(tool.Name), Toggle)))));
    }

    public ObservableCollection<ToolGroupViewModel> Groups { get; }

    public string ToolCountText => $"{_host.Runtime.Tools.Count} 个内置工具";

    [RelayCommand]
    private void Back() => _host.OpenHome();

    private void Toggle(string name, bool enabled)
    {
        var settings = _host.Settings;
        settings.DisabledTools.RemoveAll(item => item == name);
        if (!enabled)
        {
            settings.DisabledTools.Add(name);
        }

        _ = _host.SettingsProvider.SaveAsync(settings);
    }
}
