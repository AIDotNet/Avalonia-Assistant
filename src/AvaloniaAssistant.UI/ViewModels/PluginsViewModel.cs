using System.Collections.ObjectModel;
using Avalonia.Media;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>已安装的插件卡：名字、来源、作用域、扩展入口，可停用、可升级、可卸载。</summary>
public sealed partial class InstalledPluginViewModel : ObservableObject
{
    public InstalledPluginViewModel(AgentPluginInfo info)
    {
        Info = info;
        PathText = Ui.ShortenPath(info.InstalledPath ?? string.Empty);
        IsEnabled = info.IsEnabled;
    }

    public AgentPluginInfo Info { get; }

    public string Name => Info.Name;

    public string Source => Info.Source;

    public bool IsProjectScope => Info.IsProjectScope;

    public string ScopeText => IsProjectScope ? "项目级" : "用户级";

    public string PathText { get; }

    public string VersionText => string.IsNullOrWhiteSpace(Info.Version) ? string.Empty : "v" + Info.Version;

    public bool HasVersion => VersionText.Length > 0;

    /// <summary>settings.json 里有记录、磁盘上没有包：下次会话启动会重试安装，别显示得像装好了。</summary>
    public bool IsMissing => !Info.IsInstalled;

    /// <summary>停用的包 pi 不加载任何资源，但记录还在，随时能开回来。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EnableActionText))]
    [NotifyPropertyChangedFor(nameof(DetailBrush))]
    [NotifyPropertyChangedFor(nameof(ExtensionsText))]
    private bool isEnabled;

    public string EnableActionText => IsEnabled ? "停用" : "启用";

    /// <summary>扩展入口。没有扩展的包（只带技能/提示词/主题）也要说清楚，不然像装失败了。</summary>
    public string ExtensionsText => !IsEnabled
        ? "已停用：包还在清单里，pi 不会加载它的任何扩展 / 技能 / 提示词 / 主题"
        : IsMissing
            ? "没装上：settings.json 有记录，磁盘上没有这个包（下次会话启动会重试）"
            : Info.Extensions.Count == 0
                ? "没有扩展入口（技能 / 提示词 / 主题类包）"
                : $"扩展 {Info.Extensions.Count} 个 · {string.Join("、", Info.Extensions)}";

    public IBrush DetailBrush => IsMissing
        ? Brush.Parse("#A15C07")
        : IsEnabled ? Brush.Parse("#6F6F69") : Brush.Parse("#9C9C94");

    [ObservableProperty]
    private bool isConfirmingRemove;

    /// <summary>npm / git 上检测到新版本。</summary>
    [ObservableProperty]
    private bool hasUpdate;

    [ObservableProperty]
    private bool isWorking;

    [RelayCommand]
    private void StartRemove() => IsConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;

    [RelayCommand]
    private void OpenFolder()
    {
        if (!string.IsNullOrWhiteSpace(Info.InstalledPath))
        {
            Ui.OpenExternal(Info.InstalledPath);
        }
    }
}

/// <summary>市场里的一张卡。安装前要用户点一次确认：扩展是能读文件、能起进程的代码。</summary>
public sealed partial class MarketPluginViewModel : ObservableObject
{
    public MarketPluginViewModel(AgentPluginMarketItem item, bool installed)
    {
        Item = item;
        IsInstalled = installed;
    }

    public AgentPluginMarketItem Item { get; }

    public string Name => Item.Name;

    public string Version => string.IsNullOrWhiteSpace(Item.Version) ? "" : "v" + Item.Version;

    public bool HasVersion => Version.Length > 0;

    public string Description => string.IsNullOrWhiteSpace(Item.Description) ? "（这个包没写描述）" : Item.Description;

    public string Publisher => string.IsNullOrWhiteSpace(Item.Publisher) ? "未知发布者" : Item.Publisher;

    public string PublishedText => Item.PublishedAt.Length >= 10 ? Item.PublishedAt[..10] : Item.PublishedAt;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool isInstalled;

    /// <summary>确认态：卡片下半部分就地展开一句风险说明。</summary>
    [ObservableProperty]
    private bool isConfirming;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActionText))]
    [NotifyPropertyChangedFor(nameof(CanInstall))]
    private bool isInstalling;

    [ObservableProperty]
    private string? progress;

    public bool CanInstall => !IsInstalled && !IsInstalling;

    public string ActionText => IsInstalled ? "已安装" : IsInstalling ? "安装中…" : "安装";

    [RelayCommand]
    private void StartInstall()
    {
        if (CanInstall)
        {
            IsConfirming = true;
        }
    }

    [RelayCommand]
    private void CancelInstall() => IsConfirming = false;

    /// <summary>卡片上只有三行字，想看 README、依赖和更新时间得去 npm 页面。</summary>
    [RelayCommand]
    private void OpenPage()
    {
        if (!string.IsNullOrWhiteSpace(Item.Url))
        {
            Ui.OpenExternal(Item.Url);
        }
    }
}

/// <summary>
/// 插件市场。和「技能」「MCP」是同一层的三件事：技能是 SKILL.md，MCP 是一份 mcp.json，
/// 插件是 pi 自己的包（npm / git / 本地目录），可以同时带扩展、技能、提示词和主题。
/// </summary>
/// <remarks>
/// 装什么、装到哪、写进哪个字段，全部交给 Pi.CodingAgent 的 PackageManager：
/// 这样界面上装出来的结果和命令行 <c>pi install</c> 完全一致，不会出现「界面装完 pi 读不到」。
/// </remarks>
public sealed partial class PluginsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _host;

    /// <summary>同一时刻只让一个安装 / 卸载 / 更新跑：并发会抢同一个 npm 目录的锁。</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>搜索序号。慢请求回来时如果已经不是最后一次搜索，结果直接丢掉。</summary>
    private int _searchToken;

    /// <summary>当前关键词 / 是否还有下一页。加载更多沿用同一个关键词。</summary>
    private string _query = string.Empty;
    private bool _hasMore;

    /// <summary>检测到新版本的源。刷新清单时按它给卡片打标。</summary>
    private readonly HashSet<string> _updates = new(StringComparer.OrdinalIgnoreCase);

    public PluginsViewModel(MainWindowViewModel host)
    {
        _host = host;
        Refresh();
        _ = SearchAsync(string.Empty, reset: true);
    }

    public ObservableCollection<InstalledPluginViewModel> Installed { get; } = [];

    public ObservableCollection<MarketPluginViewModel> Market { get; } = [];

    public ObservableCollection<AgentExtensionInfo> ManualExtensions { get; } = [];

    public bool HasInstalled => Installed.Count > 0;

    public bool HasManualExtensions => ManualExtensions.Count > 0;

    public bool HasMarket => Market.Count > 0;

    /// <summary>空结果提示：正在搜的时候不摆「没有搜到」，那会像搜索失败了。</summary>
    public bool ShowMarketEmpty => !IsSearchingMarket && !HasMarket;

    /// <summary>
    /// 已安装 / 市场 两个分页。原来两块上下叠着，标题、卡片、按钮互相抢，
    /// 用户还得自己判断哪块是主体。默认停在「已安装」——装完就该看见它。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowingInstalled))]
    private bool showingMarket;

    public bool ShowingInstalled => !ShowingMarket;

    [RelayCommand]
    private void ShowInstalledTab() => ShowingMarket = false;

    [RelayCommand]
    private void ShowMarketTab() => ShowingMarket = true;

    /// <summary>还有下一页时底部给一个「加载更多」。</summary>
    public bool HasMore => _hasMore;

    public string LoadMoreText => $"加载更多（已显示 {Market.Count} 个）";

    /// <summary>标题下的计数。有没装上的、停用的要单独说出来，不然和卡片上的状态自相矛盾。</summary>
    public string InstalledCountText
    {
        get
        {
            if (Installed.Count == 0)
            {
                return "还没有装插件";
            }

            var parts = new List<string> { $"{Installed.Count} 个插件" };
            var disabled = Installed.Count(item => !item.IsEnabled);
            if (disabled > 0)
            {
                parts.Add($"{disabled} 个已停用");
            }

            var missing = Installed.Count(item => item.IsMissing);
            if (missing > 0)
            {
                parts.Add($"{missing} 个没装上");
            }

            return string.Join(" · ", parts);
        }
    }

    public string MarketCountText => IsSearchingMarket
        ? "正在查 npm…"
        : string.IsNullOrWhiteSpace(SearchText)
            ? $"热门 {Market.Count} 个"
            : $"命中 {Market.Count} 个";

    public string PackagesRootText => Ui.ShortenPath(_host.Plugins.PackagesRoot);

    /// <summary>安装走本机 npm，源在设置页可改；把当前用的源写出来，省得装不动时不知道在等谁。</summary>
    public string InstallHint
    {
        get
        {
            var registry = _host.Settings.NpmRegistry;
            var source = string.IsNullOrWhiteSpace(registry)
                ? "npm 默认源"
                : registry.Replace("https://", string.Empty, StringComparison.OrdinalIgnoreCase)
                    .TrimEnd('/');
            return "裸包名按 npm 处理；也可以写 npm:（可带版本）、git:、https 地址或本地目录。"
                   + $"执行的是本机的 npm / git，源：{source}。";
        }
    }

    public string ExtensionsRootText => Ui.ShortenPath(_host.Plugins.ExtensionsRoot);

    /// <summary>有来源任务时写清返回哪里：从对话页进来的，返回不该落在首页。</summary>
    public string BackHint => _host.ReturnHint;

    public bool HasReturnTarget => _host.HasReturnTarget;

    [RelayCommand]
    private void Back() => _host.GoBack();

    // ---- 安装位置 ----

    /// <summary>true = 装到当前工作区的 <c>.pi</c>；false = 装到用户目录（默认）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InstallScopeText))]
    [NotifyPropertyChangedFor(nameof(InstallScopeHint))]
    private bool installToWorkspace;

    public bool CanInstallToWorkspace => !string.IsNullOrWhiteSpace(_host.Settings.WorkspacePath);

    public string InstallScopeText => InstallToWorkspace ? "装到当前工作区" : "装到用户目录";

    public string InstallScopeHint => CanInstallToWorkspace
        ? "项目级包只在当前工作区生效，写进 .pi/settings.json"
        : "还没有选工作区，只能装到用户目录";

    [RelayCommand]
    private void ToggleInstallScope()
    {
        if (!CanInstallToWorkspace)
        {
            ShowNotice("先选一个工作区，才能装项目级插件。", isError: true);
            return;
        }

        InstallToWorkspace = !InstallToWorkspace;
    }

    // ---- 搜索 ----

    [ObservableProperty]
    private string searchText = string.Empty;

    /// <summary>
    /// 搜索框被清空（× 或全删）就回到热门列表。
    /// 否则标题写着「热门 N 个」，下面还是上一次的筛选结果，两边对不上。
    /// </summary>
    partial void OnSearchTextChanged(string value)
    {
        if (string.IsNullOrWhiteSpace(value) && !IsSearchingMarket)
        {
            _ = SearchAsync(string.Empty, reset: true);
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarketCountText))]
    [NotifyPropertyChangedFor(nameof(ShowMarketEmpty))]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    private bool isSearchingMarket;

    [RelayCommand]
    private Task Search() => SearchAsync(SearchText, reset: true);

    [RelayCommand]
    private Task LoadMore() => SearchAsync(_query, reset: false);

    [RelayCommand]
    private void ClearSearch()
    {
        if (SearchText.Length == 0)
        {
            // 本来就是空的：清空按钮不会触发变更事件，直接补一次热门搜索
            _ = SearchAsync(string.Empty, reset: true);
            return;
        }

        SearchText = string.Empty;
    }

    private async Task SearchAsync(string query, bool reset)
    {
        if (!reset && (!_hasMore || IsSearchingMarket))
        {
            return;
        }

        var token = ++_searchToken;
        var from = reset ? 0 : Market.Count;
        _query = query;
        IsSearchingMarket = true;
        if (reset)
        {
            Notice = null;
        }

        try
        {
            var page = await _host.Plugins.SearchAsync(query, from);
            if (token != _searchToken)
            {
                return;
            }

            var installed = InstalledNames();
            if (reset)
            {
                Market.Clear();
            }

            foreach (var item in page.Items)
            {
                if (Market.Any(existing => string.Equals(existing.Name, item.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Market.Add(new MarketPluginViewModel(item, installed.Contains(item.Name)));
            }

            _hasMore = page.Total > Market.Count;
            OnPropertyChanged(nameof(HasMore));
            OnPropertyChanged(nameof(LoadMoreText));
            OnPropertyChanged(nameof(MarketCountText));
            OnPropertyChanged(nameof(HasMarket));
            OnPropertyChanged(nameof(ShowMarketEmpty));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (token == _searchToken)
            {
                Market.Clear();
                _hasMore = false;
                ShowNotice($"市场连不上：{ex.Message}。可以直接用「手动安装」填包名或 git 地址。", isError: true);
                OnPropertyChanged(nameof(HasMarket));
                OnPropertyChanged(nameof(ShowMarketEmpty));
                OnPropertyChanged(nameof(HasMore));
            }
        }
        finally
        {
            if (token == _searchToken)
            {
                IsSearchingMarket = false;
                OnPropertyChanged(nameof(MarketCountText));
            }
        }
    }

    private HashSet<string> InstalledNames()
        => Installed.Select(item => item.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

    // ---- 清单 ----

    [RelayCommand]
    private void Refresh()
    {
        Installed.Clear();
        foreach (var plugin in _host.Plugins.ListInstalled())
        {
            Installed.Add(new InstalledPluginViewModel(plugin) { HasUpdate = _updates.Contains(plugin.Source) });
        }

        ManualExtensions.Clear();
        foreach (var extension in _host.Plugins.ListManualExtensions())
        {
            ManualExtensions.Add(extension);
        }

        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(HasManualExtensions));
        OnPropertyChanged(nameof(InstalledCountText));
        OnPropertyChanged(nameof(CanInstallToWorkspace));
        OnPropertyChanged(nameof(InstallScopeHint));

        // 装完 / 卸完之后市场里的按钮状态要跟着变，不用重新联网。
        var names = InstalledNames();
        foreach (var item in Market)
        {
            item.IsInstalled = names.Contains(item.Name);
        }
    }

    // ---- 启用 / 停用 ----

    [RelayCommand]
    private async Task ToggleEnabledAsync(InstalledPluginViewModel? item)
    {
        if (item is null || item.IsWorking)
        {
            return;
        }

        if (!await _gate.WaitAsync(0))
        {
            ShowNotice("有一个安装 / 卸载正在进行，等它结束。", isError: true);
            return;
        }

        item.IsWorking = true;
        var enable = !item.IsEnabled;
        try
        {
            var result = await _host.Plugins.SetEnabledAsync(item.Info.Source, item.Info.IsProjectScope, enable);
            if (!result.Ok)
            {
                ShowNotice($"改不了：{result.Detail}", isError: true);
                return;
            }

            _host.Runtime.InvalidateSessions();
            Refresh();
            ShowNotice(
                enable
                    ? $"已启用 {item.Name}，下一条消息生效。"
                    : $"已停用 {item.Name}。包还在 settings.json 里，扩展和技能都不再加载。",
                isError: false);
        }
        catch (Exception ex)
        {
            ShowNotice($"改不了：{ex.Message}", isError: true);
        }
        finally
        {
            item.IsWorking = false;
            _gate.Release();
        }
    }

    // ---- 更新 ----

    [ObservableProperty]
    private bool isCheckingUpdates;

    public string UpdateSummary => _updates.Count == 0 ? string.Empty : $"{_updates.Count} 个可更新";

    [RelayCommand]
    private async Task CheckUpdatesAsync()
    {
        if (IsCheckingUpdates)
        {
            return;
        }

        IsCheckingUpdates = true;
        try
        {
            var updates = await _host.Plugins.CheckUpdatesAsync();
            _updates.Clear();
            foreach (var update in updates)
            {
                _updates.Add(update.Source);
            }

            foreach (var card in Installed)
            {
                card.HasUpdate = _updates.Contains(card.Info.Source);
            }

            OnPropertyChanged(nameof(UpdateSummary));
            ShowNotice(
                updates.Count == 0
                    ? "都装的是最新版。"
                    : $"有 {updates.Count} 个包可以更新：{string.Join('、', updates.Select(update => update.Name))}。",
                isError: false);
        }
        catch (Exception ex)
        {
            ShowNotice($"检查更新失败：{ex.Message}", isError: true);
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    [RelayCommand]
    private async Task UpdateAsync(InstalledPluginViewModel? item)
    {
        if (item is null || item.IsWorking)
        {
            return;
        }

        if (!await _gate.WaitAsync(0))
        {
            ShowNotice("有一个安装 / 卸载正在进行，等它结束。", isError: true);
            return;
        }

        item.IsWorking = true;
        try
        {
            // 进度只在更新期间更新，迟到的投递不能把结果盖掉。
            var progress = new Progress<string>(text =>
            {
                if (item.IsWorking)
                {
                    ShowNotice(text, isError: false);
                }
            });

            var result = await _host.Plugins.UpdateAsync(item.Info.Source, progress);
            if (!result.Ok)
            {
                ShowNotice($"更新失败：{result.Detail}", isError: true);
                return;
            }

            _updates.Remove(item.Info.Source);
            _host.Runtime.InvalidateSessions();
            Refresh();
            ShowNotice($"已更新 {item.Name}，下一条消息生效。", isError: false);
        }
        catch (Exception ex)
        {
            ShowNotice($"更新失败：{ex.Message}", isError: true);
        }
        finally
        {
            item.IsWorking = false;
            _gate.Release();
        }
    }

    // ---- 手动安装 ----

    [ObservableProperty]
    private bool isManualOpen;

    [ObservableProperty]
    private string manualSource = string.Empty;

    /// <summary>手动安装进行中。进度回调只在为 true 时更新提示条，避免结束后又冒出一行旧进度。</summary>
    private bool ManualInstalling { get; set; }

    [RelayCommand]
    public void ShowManual()
    {
        IsManualOpen = true;
        Notice = null;
    }

    [RelayCommand]
    private void CancelManual()
    {
        IsManualOpen = false;
        ManualSource = string.Empty;
    }

    [RelayCommand]
    private async Task InstallManualAsync()
    {
        if (string.IsNullOrWhiteSpace(ManualSource))
        {
            ShowNotice("填一个包名、npm: 版本号、git 地址，或者本地目录。", isError: true);
            return;
        }

        var source = ManualSource.Trim();
        ManualInstalling = true;
        try
        {
            var progress = new Progress<string>(text =>
            {
                if (ManualInstalling)
                {
                    ShowNotice(text, isError: false);
                }
            });

            if (await InstallAsync(source, InstallToWorkspace, progress))
            {
                IsManualOpen = false;
                ManualSource = string.Empty;
            }
        }
        finally
        {
            ManualInstalling = false;
        }
    }

    // ---- 市场安装 ----

    [RelayCommand]
    private async Task InstallAsync(MarketPluginViewModel? item)
    {
        if (item is null || !item.CanInstall)
        {
            return;
        }

        item.IsInstalling = true;
        var progress = new Progress<string>(text =>
        {
            // 结束之后才到的投递不再落到卡片上（否则「安装中…」会留着不走）
            if (item.IsInstalling)
            {
                item.Progress = text;
            }
        });

        try
        {
            if (await InstallAsync("npm:" + item.Name, InstallToWorkspace, progress))
            {
                item.IsInstalled = true;
            }
        }
        finally
        {
            item.IsInstalling = false;
            item.IsConfirming = false;
            item.Progress = null;
        }
    }

    /// <summary>安装的唯一入口：串行、失败把 npm 的原话带回来、成功后丢掉旧会话。</summary>
    private async Task<bool> InstallAsync(string source, bool projectScope, IProgress<string>? progress)
    {
        if (!await _gate.WaitAsync(0))
        {
            ShowNotice("已经有一个安装在跑，等它结束再装下一个。", isError: true);
            return false;
        }

        try
        {
            ShowNotice($"正在安装 {source}…", isError: false);
            var result = await _host.Plugins.InstallAsync(source, projectScope, progress);

            if (!result.Ok)
            {
                ShowNotice($"装不上 {result.Source}：{result.Detail}", isError: true);
                return false;
            }

            // 插件是会话装配时读的：不清缓存就得等重启。
            _host.Runtime.InvalidateSessions();
            Refresh();

            var installedName = Installed
                .FirstOrDefault(item => string.Equals(item.Source, result.Source, StringComparison.OrdinalIgnoreCase))
                ?.Name;

            ShowNotice(
                $"已安装 {installedName ?? result.Source}，写进"
                + (projectScope ? "工作区的 .pi/settings.json" : " settings.json")
                + "。下一条消息生效——会重建会话上下文，长任务可能要重放一遍。",
                isError: false);
            return true;
        }
        catch (Exception ex)
        {
            ShowNotice($"装不上 {source}：{ex.Message}", isError: true);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ---- 卸载 ----

    [RelayCommand]
    private async Task RemoveAsync(InstalledPluginViewModel? item)
    {
        if (item is null)
        {
            return;
        }

        if (!await _gate.WaitAsync(0))
        {
            ShowNotice("有一个安装 / 卸载正在进行，等它结束。", isError: true);
            return;
        }

        item.IsWorking = true;
        try
        {
            var result = await _host.Plugins.RemoveAsync(item.Info.Source, item.Info.IsProjectScope);
            if (!result.Ok)
            {
                ShowNotice($"卸载失败：{result.Detail}", isError: true);
                return;
            }

            _updates.Remove(item.Info.Source);
            _host.Runtime.InvalidateSessions();
            Refresh();
            ShowNotice($"已卸载 {item.Name}。settings.json 里的包记录同时删掉了。", isError: false);
        }
        catch (Exception ex)
        {
            ShowNotice($"卸载失败：{ex.Message}", isError: true);
        }
        finally
        {
            item.IsWorking = false;
            _gate.Release();
        }
    }

    // ---- 目录 ----

    [RelayCommand]
    private void OpenPackagesFolder() => OpenFolder(_host.Plugins.PackagesRoot);

    [RelayCommand]
    private void OpenExtensionsFolder() => OpenFolder(_host.Plugins.ExtensionsRoot);

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowNotice("目录不可用：" + ex.Message, isError: true);
            return;
        }

        if (!Ui.OpenExternal(path))
        {
            ShowNotice("打不开这个目录，可能被系统策略拦住了。", isError: true);
        }
    }

    // ---- 提示条 ----

    [ObservableProperty]
    private string? notice;

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    public bool NoticeIsError { get; private set; }

    public IBrush NoticeBackground => Brush.Parse(NoticeIsError ? "#FBEAE8" : "#EAF4EA");

    public IBrush NoticeForeground => Brush.Parse(NoticeIsError ? "#B3261E" : "#2E7D32");

    partial void OnNoticeChanged(string? value) => OnPropertyChanged(nameof(HasNotice));

    private void ShowNotice(string message, bool isError)
    {
        NoticeIsError = isError;
        Notice = message;
        OnPropertyChanged(nameof(NoticeIsError));
        OnPropertyChanged(nameof(NoticeBackground));
        OnPropertyChanged(nameof(NoticeForeground));
    }
}
