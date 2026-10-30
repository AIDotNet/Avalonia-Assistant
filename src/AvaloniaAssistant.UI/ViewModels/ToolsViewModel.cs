using System.Collections.ObjectModel;
using Avalonia.Media;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
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

/// <summary>
/// 插件带来的工具。它没有本地元数据（是不是只读、会不会起进程都写在扩展代码里），
/// 所以统一按「调用要确认」展示，关掉就是不进会话的 allowlist。
/// </summary>
public sealed partial class PluginToolItemViewModel : ObservableObject
{
    private readonly ToolToggle _toggle;
    private bool _initializing = true;

    public PluginToolItemViewModel(AgentPluginToolInfo info, bool enabled, ToolToggle toggle)
    {
        Info = info;
        _toggle = toggle;
        IsEnabled = enabled;
        _initializing = false;
    }

    public AgentPluginToolInfo Info { get; }

    public string Name => Info.Name;

    public string Plugin => Info.Plugin;

    public string Description => string.IsNullOrWhiteSpace(Info.Description) ? "（这个工具没写描述）" : Info.Description;

    public string ExposureText => Info.Exposure;

    public bool RequiresSearch => Info.RequiresSearch;

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
        Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || Plugin.Contains(keyword, StringComparison.OrdinalIgnoreCase)
        || Description.Contains(keyword, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 技能卡片。开关只改「这一轮要不要把它放进系统提示」，不碰磁盘上的 SKILL.md；
/// 卡片本身还能点开看正文——以前只能看到两行描述，想确认它到底干什么得去资源管理器翻。
/// </summary>
public sealed partial class SkillItemViewModel : ObservableObject
{
    private readonly ToolToggle _toggle;
    private bool _initializing = true;

    public SkillItemViewModel(AgentSkillInfo info, bool enabled, ToolToggle toggle)
    {
        Info = info;
        _toggle = toggle;
        IsEnabled = enabled;
        _initializing = false;
    }

    public AgentSkillInfo Info { get; }

    public string Name => Info.Name;

    public string Description => Info.Description;

    public string Directory => Info.Directory;

    public bool IsProjectScope => Info.IsProjectScope;

    public string ScopeText => IsProjectScope ? "项目级" : "用户级";

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

public sealed partial class ToolsViewModel : ObservableObject
{
    private const string AllCategory = "全部";

    private readonly MainWindowViewModel _host;
    private readonly List<ToolItemViewModel> _all = [];
    private readonly List<PluginToolItemViewModel> _pluginTools = [];
    private string _skillsRoot = string.Empty;
    private string _skillsRootRaw = string.Empty;

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

        // 插件工具要真加载一次扩展才知道有哪些（.ts 走进程内转译，1~3 秒），
        // 所以页面先出来、清单后到，不把整页卡住。
        _ = LoadPluginToolsAsync();
    }

    /// <summary>分类名（全部 / 系统 / …），直接喂给 ChipGroup。</summary>
    public ObservableCollection<string> Categories { get; }

    public ObservableCollection<ToolItemViewModel> VisibleTools { get; } = [];

    public ObservableCollection<SkillItemViewModel> Skills { get; } = [];

    /// <summary>按搜索词过滤后的技能。搜索框写着「搜索技能」，技能卡片必须跟着一起筛。</summary>
    public ObservableCollection<SkillItemViewModel> VisibleSkills { get; } = [];

    public ObservableCollection<PluginToolItemViewModel> PluginTools { get; } = [];

    public ObservableCollection<PluginToolItemViewModel> VisiblePluginTools { get; } = [];

    /// <summary>插件工具正在读扩展清单。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginToolsHint))]
    private bool isLoadingPluginTools;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PluginToolsHint))]
    private string? pluginToolsError;

    public bool HasPluginTools => PluginTools.Count > 0;

    public string PluginToolsHint => IsLoadingPluginTools
        ? "正在读插件清单（要真加载一次扩展）…"
        : PluginToolsError is { Length: > 0 } error
            ? "读不到插件工具：" + error
            : PluginTools.Count == 0
                ? "还没有插件提供工具。装一个插件后回到这里，它的工具会出现在下面。"
                : $"{PluginTools.Count} 个插件工具 · 关掉后不进会话的 allowlist，下一条消息生效";

    public bool HasSkills => Skills.Count > 0;

    public string SkillsEmptyHint => string.IsNullOrEmpty(_skillsRoot)
        ? "技能目录不可用。"
        : "还没有技能。可以从文件夹或 zip 导入，也会读技能目录里的 <名称>/SKILL.md。";

    /// <summary>技能目录，用户目录已缩成 ~。</summary>
    public string SkillsRootText => _skillsRoot;

    /// <summary>技能数量。搜索时显示「命中 / 总数」，免得以为技能被删了。</summary>
    public string CapabilityText => string.IsNullOrWhiteSpace(SearchText)
        ? $"{Skills.Count} 个技能"
        : $"{VisibleSkills.Count} / {Skills.Count} 个技能";

    public string SubtitleText => PluginTools.Count == 0
        ? $"{_all.Count} 个内置工具 · {_all.Count(tool => tool.IsEnabled)} 个已启用"
        : $"{_all.Count} 个内置工具 · {PluginTools.Count} 个插件工具";

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private string? selectedCategory;

    [ObservableProperty]
    private bool hasNothing;

    [ObservableProperty]
    private string? notice;

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    public bool NoticeIsError { get; private set; }

    /// <summary>提示条配色：装成功是绿的，失败是红的。</summary>
    public IBrush NoticeBackground => Brush.Parse(NoticeIsError ? "#FBEAE8" : "#EAF4EA");

    public IBrush NoticeForeground => Brush.Parse(NoticeIsError ? "#B3261E" : "#2E7D32");

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    partial void OnSearchTextChanged(string value) => Refresh();

    partial void OnSelectedCategoryChanged(string? value) => Refresh();

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>有来源任务时写清返回哪里：从对话页来查技能，返回不该落在首页。</summary>
    public string BackHint => _host.ReturnHint;

    /// <summary>页头那条面包屑显不显示：有「刚才那条任务」才显示。</summary>
    public bool HasReturnTarget => _host.HasReturnTarget;

    [RelayCommand]
    private void Back() => _host.GoBack();

    /// <summary>选一个含 SKILL.md 的文件夹，复制进技能目录。</summary>
    [RelayCommand]
    private async Task InstallSkillAsync()
    {
        var folder = await _host.Picker.PickFolderAsync("选择技能文件夹（里面要有 SKILL.md）");
        if (!string.IsNullOrWhiteSpace(folder))
        {
            Install(() => CapabilityInstaller.InstallSkill(folder, _skillsRootRaw));
        }
    }

    /// <summary>选一个 zip 技能包，解开后复制进技能目录。</summary>
    [RelayCommand]
    private async Task InstallSkillZipAsync()
    {
        var file = await _host.Picker.PickFileAsync("选择技能包（zip 里要有 SKILL.md）", "*.zip");
        if (!string.IsNullOrWhiteSpace(file))
        {
            Install(() => CapabilityInstaller.InstallSkillZip(file, _skillsRootRaw));
        }
    }

    private void Install(Func<string> install)
    {
        if (string.IsNullOrWhiteSpace(_skillsRootRaw))
        {
            ShowNotice("技能目录不可用，先选一个工作区。", isError: true);
            return;
        }

        try
        {
            var target = install();
            _host.Runtime.InvalidateSessions();
            RefreshCapabilities();
            ShowNotice($"已装到 {Ui.ShortenPath(target)}，下一个会话生效。", isError: false);
        }
        catch (Exception ex)
        {
            ShowNotice(ex.Message, isError: true);
        }
    }

    [RelayCommand]
    private void OpenSkillsFolder()
    {
        if (string.IsNullOrWhiteSpace(_skillsRootRaw))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_skillsRootRaw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice("技能目录不可用：" + ex.Message, isError: true);
            return;
        }

        if (!Ui.OpenExternal(_skillsRootRaw))
        {
            ShowNotice("打不开技能目录，可能被系统策略拦住了。", isError: true);
        }
    }

    private void ShowNotice(string message, bool isError)
    {
        NoticeIsError = isError;
        Notice = message;
        OnPropertyChanged(nameof(NoticeIsError));
        OnPropertyChanged(nameof(NoticeBackground));
        OnPropertyChanged(nameof(NoticeForeground));
    }

    [RelayCommand]
    private void RefreshCapabilities()
    {
        var capabilities = _host.Runtime.Capabilities;
        _skillsRootRaw = capabilities.SkillsRoot;
        _skillsRoot = Ui.ShortenPath(capabilities.SkillsRoot);

        var disabled = _host.Settings.DisabledSkills;
        Skills.Clear();
        foreach (var skill in capabilities.Skills)
        {
            Skills.Add(new SkillItemViewModel(
                skill,
                !disabled.Contains(skill.Name, StringComparer.OrdinalIgnoreCase),
                ToggleSkill));
        }

        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(SkillsEmptyHint));
        OnPropertyChanged(nameof(SkillsRootText));

        // 技能集合换了，过滤结果要跟着重算（构造函数里也是先扫目录再 Refresh）。
        Refresh();
    }

    private void Refresh()
    {
        VisibleTools.Clear();
        VisibleSkills.Clear();
        VisiblePluginTools.Clear();

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

        foreach (var skill in Skills)
        {
            if (keyword.Length == 0 || Matches(skill, keyword))
            {
                VisibleSkills.Add(skill);
            }
        }

        // 插件工具不参与上面的分类筛选：它们的来源是插件，不是「文件 / 命令 / 网络」这些内置分类。
        foreach (var tool in _pluginTools)
        {
            if (keyword.Length == 0 || tool.Matches(keyword))
            {
                VisiblePluginTools.Add(tool);
            }
        }

        HasNothing = VisibleTools.Count == 0 && VisibleSkills.Count == 0 && VisiblePluginTools.Count == 0;
        OnPropertyChanged(nameof(SubtitleText));
        OnPropertyChanged(nameof(CapabilityText));
        OnPropertyChanged(nameof(HasPluginTools));
    }

    private static bool Matches(SkillItemViewModel skill, string keyword)
        => skill.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase)
           || skill.Description.Contains(keyword, StringComparison.OrdinalIgnoreCase);

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

    private void ToggleSkill(string name, bool enabled)
    {
        var settings = _host.Settings;
        settings.DisabledSkills.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            settings.DisabledSkills.Add(name);
        }

        _ = _host.SettingsProvider.SaveAsync(settings);
        ShowNotice(
            enabled
                ? $"已启用「{name}」：下一轮生效。"
                : $"已关掉「{name}」：下一轮不再把它放进系统提示，SKILL.md 还在原处。",
            isError: false);
    }

    /// <summary>
    /// 读插件工具。真加载一次扩展才知道插件注册了什么（TS 要过一遍进程内转译），
    /// 所以这是显式命令 + 首次进页面自动跑一次。
    /// </summary>
    [RelayCommand]
    private async Task LoadPluginToolsAsync()
    {
        if (IsLoadingPluginTools)
        {
            return;
        }

        IsLoadingPluginTools = true;
        PluginToolsError = null;
        try
        {
            // 加载扩展（Jint 转译 TypeScript）是纯 CPU 活，放在 UI 线程上会把首帧卡住 2 秒以上，
            // 页面看起来像没打开。丢到线程池，清单回来了再更新绑定。
            var tools = await Task.Run(() => _host.Plugins.ListToolsAsync());
            var disabled = _host.Settings.DisabledTools;

            _pluginTools.Clear();
            PluginTools.Clear();
            foreach (var info in tools)
            {
                var item = new PluginToolItemViewModel(info, !disabled.Contains(info.Name), TogglePluginTool);
                _pluginTools.Add(item);
                PluginTools.Add(item);
            }

            Refresh();
            OnPropertyChanged(nameof(PluginToolsHint));
            OnPropertyChanged(nameof(SubtitleText));
        }
        catch (Exception ex)
        {
            PluginToolsError = ex.Message;
            ShowNotice("读不到插件工具：" + ex.Message, isError: true);
        }
        finally
        {
            IsLoadingPluginTools = false;
            OnPropertyChanged(nameof(PluginToolsHint));
        }
    }

    /// <summary>
    /// 插件工具没有本地元数据，关掉就是把名字从会话的 allowlist 里摘掉（settings.json 的 disabledTools）。
    /// </summary>
    private void TogglePluginTool(string name, bool enabled)
    {
        var settings = _host.Settings;
        settings.DisabledTools.RemoveAll(item => string.Equals(item, name, StringComparison.OrdinalIgnoreCase));
        if (!enabled)
        {
            settings.DisabledTools.Add(name);
        }

        _ = _host.SettingsProvider.SaveAsync(settings);
        _host.Runtime.InvalidateSessions();
        ShowNotice(
            enabled
                ? $"已启用插件工具「{name}」：下一条消息生效。"
                : $"已关掉插件工具「{name}」：下一条消息起它就进不了模型的工具清单。",
            isError: false);
    }

    /// <summary>读出 SKILL.md 原文，交给视图弹窗。读文件放 VM，视图只管显示。</summary>
    [RelayCommand]
    private async Task PreviewSkillAsync(SkillItemViewModel? skill)
    {
        if (skill is null)
        {
            return;
        }

        var path = Path.Combine(skill.Directory, "SKILL.md");
        try
        {
            var content = await File.ReadAllTextAsync(path);
            PreviewRequested?.Invoke(this, new SkillPreview(skill.Name, path, content));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowNotice($"读不了 {path}：{ex.Message}", isError: true);
        }
    }

    public event EventHandler<SkillPreview>? PreviewRequested;
}

/// <summary>技能正文弹窗要的东西：名字、路径、原文。</summary>
public sealed record SkillPreview(string Name, string Path, string Content);

/// <summary>给分类配一套图标和配色，让卡片像一列有辨识度的技能。</summary>
internal static class ToolPalette
{
    private sealed record Entry(string[] Categories, Func<Geometry> Glyph, string Accent, string Tile);

    private static readonly Entry[] Entries =
    [
        new(["文件"], () => ArcIcons.Folder, "#1F9254", "#E8F6EE"),
        new(["命令"], () => ArcIcons.Terminal, "#7B4DE0", "#F1EBFE"),
        new(["网络"], () => ArcIcons.Globe, "#0E8A96", "#E4F5F7"),
        new(["时间"], () => ArcIcons.Clock, "#B07A12", "#FDF3DE"),
        new(["技能"], () => ArcIcons.Sparkles, "#C2417B", "#FCE9F1"),
        new(["任务"], () => ArcIcons.Play, "#2F6FED", "#E8F0FE"),
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
