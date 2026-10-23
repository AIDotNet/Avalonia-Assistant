using System.Collections.ObjectModel;
using Avalonia.Media;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>工具卡片：图标配色 + 标题 + 一句描述 + 审批/可写/参数三个标签 + 开关。</summary>
public sealed partial class ToolItemViewModel : ObservableObject
{
    private readonly ToolToggle _toggle;
    private bool _initializing = true;

    public ToolItemViewModel(AgentToolInfo info, bool enabled, ToolToggle toggle)
    {
        Info = info;
        _toggle = toggle;
        IsEnabled = enabled;

        var palette = ToolPalette.For(info.Category, info.RequiresApproval);
        Icon = palette.Icon;
        AccentBrush = palette.Accent;
        TileBrush = palette.Tile;

        _initializing = false;
    }

    public AgentToolInfo Info { get; }

    public string Name => Info.Name;

    public string Label => Info.Label;

    public string Description => Info.Description;

    public string Category => Info.Category;

    public Geometry Icon { get; }

    public IBrush AccentBrush { get; }

    public IBrush TileBrush { get; }

    public string ApprovalText => Info.RequiresApproval ? "需要确认" : "自动执行";

    public string MutatingText => Info.IsMutating ? "会改本机" : "只读";

    public string ParameterText => Info.Parameters.Count == 0 ? "无参数" : $"{Info.Parameters.Count} 个参数";

    [ObservableProperty]
    private bool isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_initializing)
        {
            _toggle(Name, value);
        }
    }

    public bool Matches(string keyword) =>
        Label.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || Category.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}

public delegate void ToolToggle(string name, bool enabled);

public sealed partial class ToolsViewModel : ObservableObject
{
    private const string AllCategory = "全部";

    private readonly MainWindowViewModel _host;
    private readonly List<ToolItemViewModel> _all = [];
    private string _skillsRoot = string.Empty;
    private string _mcpConfigPath = string.Empty;

    public ToolsViewModel(MainWindowViewModel host)
    {
        _host = host;
        var disabled = host.Settings.DisabledTools;

        foreach (var info in host.Runtime.Tools)
        {
            _all.Add(new ToolItemViewModel(info, !disabled.Contains(info.Name), Toggle));
        }

        Categories = new ObservableCollection<string> { AllCategory };
        foreach (var category in _all.Select(tool => tool.Category).Distinct())
        {
            Categories.Add(category);
        }

        SelectedCategory = AllCategory;
        Refresh();
        RefreshCapabilities();
    }

    /// <summary>分类名（全部 / 系统 / …），直接喂给 ChipGroup。</summary>
    public ObservableCollection<string> Categories { get; }

    public ObservableCollection<ToolItemViewModel> VisibleTools { get; } = [];

    public ObservableCollection<AgentSkillInfo> Skills { get; } = [];

    public ObservableCollection<McpServerInfo> McpServers { get; } = [];

    public bool HasSkills => Skills.Count > 0;

    public bool HasMcpServers => McpServers.Count > 0;

    public string SkillsEmptyHint => string.IsNullOrEmpty(_skillsRoot)
        ? "技能目录不可用。"
        : "把 <名称>/SKILL.md 放进技能目录，下一个会话就会加载。";

    public string McpEmptyHint => string.IsNullOrEmpty(_mcpConfigPath)
        ? "MCP 配置不可用。"
        : "在配置文件里加 mcpServers，下一个会话启动时连接。";

    /// <summary>技能目录，用户目录已缩成 ~。</summary>
    public string SkillsRootText => _skillsRoot;

    /// <summary>MCP 配置文件，用户目录已缩成 ~。</summary>
    public string McpConfigText => _mcpConfigPath;

    public string CapabilityText => $"{Skills.Count} 个技能 · {McpServers.Count} 个 MCP 服务端";

    public string SubtitleText => $"{_all.Count} 个内置工具 · {_all.Count(tool => tool.IsEnabled)} 个已启用";

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string? selectedCategory;

    [ObservableProperty]
    private bool hasNothing;

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedCategoryChanged(string? value) => Refresh();

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void Back() => _host.OpenHome();

    [RelayCommand]
    private void RefreshCapabilities()
    {
        var capabilities = _host.Runtime.Capabilities;
        _skillsRoot = Ui.ShortenPath(capabilities.SkillsRoot);
        _mcpConfigPath = Ui.ShortenPath(capabilities.McpConfigPath);

        Skills.Clear();
        foreach (var skill in capabilities.Skills)
        {
            Skills.Add(skill);
        }

        McpServers.Clear();
        foreach (var server in capabilities.McpServers)
        {
            McpServers.Add(server);
        }

        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(HasMcpServers));
        OnPropertyChanged(nameof(SkillsEmptyHint));
        OnPropertyChanged(nameof(McpEmptyHint));
        OnPropertyChanged(nameof(SkillsRootText));
        OnPropertyChanged(nameof(McpConfigText));
        OnPropertyChanged(nameof(CapabilityText));
    }

    private void Refresh()
    {
        VisibleTools.Clear();

        var keyword = SearchText.Trim();
        foreach (var tool in _all)
        {
            if (!string.IsNullOrEmpty(SelectedCategory)
                && SelectedCategory != AllCategory
                && tool.Category != SelectedCategory)
            {
                continue;
            }

            if (keyword.Length > 0 && !tool.Matches(keyword))
            {
                continue;
            }

            VisibleTools.Add(tool);
        }

        HasNothing = VisibleTools.Count == 0;
        OnPropertyChanged(nameof(SubtitleText));
    }

    private void Toggle(string name, bool enabled)
    {
        var settings = _host.Settings;
        settings.DisabledTools.RemoveAll(item => item == name);
        if (!enabled)
        {
            settings.DisabledTools.Add(name);
        }

        _ = _host.SettingsProvider.SaveAsync(settings);
        OnPropertyChanged(nameof(SubtitleText));
    }
}

/// <summary>给分类配一套图标和配色，让卡片像一列有辨识度的技能。</summary>
internal static class ToolPalette
{
    private sealed record Entry(string[] Categories, Func<Geometry> Glyph, string Accent, string Tile);

    private static readonly Entry[] Entries =
    [
        new(["系统"], () => ArcIcons.Monitor, "#3B6EF6", "#EAF0FF"),
        new(["进程"], () => ArcIcons.Activity, "#E2703A", "#FDEFE6"),
        new(["文件"], () => ArcIcons.Folder, "#1F9254", "#E8F6EE"),
        new(["命令"], () => ArcIcons.Terminal, "#7B4DE0", "#F1EBFE"),
        new(["网络"], () => ArcIcons.Globe, "#0E8A96", "#E4F5F7"),
        new(["时间"], () => ArcIcons.Clock, "#B07A12", "#FDF3DE"),
        new(["技能"], () => ArcIcons.Sparkles, "#C2417B", "#FCE9F1"),
    ];

    private static readonly Dictionary<string, (Geometry Icon, IBrush Accent, IBrush Tile)> s_cache =
        new(StringComparer.Ordinal);

    public static (Geometry Icon, IBrush Accent, IBrush Tile) For(string category, bool requiresApproval)
    {
        if (s_cache.TryGetValue(category, out var cached))
        {
            return cached;
        }

        var entry = Entries.FirstOrDefault(item => item.Categories.Contains(category));
        var result = entry is null
            ? (ArcIcons.Zap, Get("#5B6472"), Get("#EFF1F4"))
            : (entry.Glyph(), Get(entry.Accent), Get(entry.Tile));

        _ = requiresApproval;
        s_cache[category] = result;
        return result;
    }

#pragma warning disable CS8603 // Geometry 来自 ArcIcons，永不为空
    private static IBrush Get(string hex) => Brush.Parse(hex);
#pragma warning restore CS8603
}
