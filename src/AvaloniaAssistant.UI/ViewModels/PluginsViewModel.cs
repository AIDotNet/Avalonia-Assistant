using System.Collections.ObjectModel;
using Avalonia.Media;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>已安装的插件卡：名字、来源、作用域、扩展入口，卸载要二次确认。</summary>
public sealed partial class InstalledPluginViewModel : ObservableObject
{
    public InstalledPluginViewModel(AgentPluginInfo info)
    {
        Info = info;
        PathText = Ui.ShortenPath(info.InstalledPath ?? string.Empty);
    }

    public AgentPluginInfo Info { get; }

    public string Name => Info.Name;

    public string Source => Info.Source;

    public bool IsProjectScope => Info.IsProjectScope;

    public string ScopeText => IsProjectScope ? "项目级" : "用户级";

    public string PathText { get; }

    /// <summary>扩展入口。没有扩展的包（只带技能/提示词/主题）也要说清楚，不然像装失败了。</summary>
    public string ExtensionsText => Info.Extensions.Count == 0
        ? "没有扩展入口（技能 / 提示词 / 主题类包）"
        : $"扩展 {Info.Extensions.Count} 个 · {string.Join("、", Info.Extensions)}";

    public bool HasExtensions => Info.Extensions.Count > 0;

    [ObservableProperty]
    private bool isConfirmingRemove;

    [RelayCommand]
    private void StartRemove() => IsConfirmingRemove = true;

    [RelayCommand]
    private void CancelRemove() => IsConfirmingRemove = false;
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

    /// <summary>同一时刻只让一个 npm 安装跑：并发跑会抢同一个 npm 目录的锁。</summary>
    private readonly SemaphoreSlim _installGate = new(1, 1);

    /// <summary>搜索序号。慢请求回来时如果已经不是最后一次搜索，结果直接丢掉。</summary>
    private int _searchToken;

    /// <summary>这一轮搜索一共命中多少条。卡片只摆前 <see cref="MaxMarketCards"/> 张，剩下的靠关键词收窄。</summary>
    private int _marketTotal;

    /// <summary>市场一次最多摆多少张卡。100 张卡挤在一个非虚拟化列表里，滚动会明显发涩。</summary>
    private const int MaxMarketCards = 40;

    public PluginsViewModel(MainWindowViewModel host)
    {
        _host = host;
        Refresh();
        _ = SearchAsync(string.Empty);
    }

    public ObservableCollection<InstalledPluginViewModel> Installed { get; } = [];

    public ObservableCollection<MarketPluginViewModel> Market { get; } = [];

    public ObservableCollection<AgentExtensionInfo> ManualExtensions { get; } = [];

    public bool HasInstalled => Installed.Count > 0;

    public bool HasManualExtensions => ManualExtensions.Count > 0;

    public bool HasMarket => Market.Count > 0;

    /// <summary>空结果提示：正在搜的时候不摆「没有搜到」，那会像搜索失败了。</summary>
    public bool ShowMarketEmpty => !IsSearchingMarket && !HasMarket;

    public string InstalledCountText => Installed.Count == 0 ? "还没有装插件" : $"{Installed.Count} 个已安装";

    public string MarketCountText => IsSearchingMarket
        ? "正在查 npm…"
        : string.IsNullOrWhiteSpace(SearchText)
            ? $"热门 {Market.Count} 个"
            : _marketTotal > Market.Count
                ? $"命中 {_marketTotal} 个 · 显示前 {Market.Count}"
                : $"命中 {_marketTotal} 个";

    public string PackagesRootText => Ui.ShortenPath(_host.Plugins.PackagesRoot);

    public string ExtensionsRootText => Ui.ShortenPath(_host.Plugins.ExtensionsRoot);

    /// <summary>有来源任务时写清返回哪里：从对话页进来的，返回不该落在首页。</summary>
    public string BackHint => _host.ReturnHint;

    public bool HasReturnTarget => _host.HasReturnTarget;

    [RelayCommand]
    private void Back() => _host.GoBack();

    // ---- 搜索 ----

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(MarketCountText))]
    [NotifyPropertyChangedFor(nameof(ShowMarketEmpty))]
    private bool isSearchingMarket;

    [RelayCommand]
    private Task Search() => SearchAsync(SearchText);

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
        _ = SearchAsync(string.Empty);
    }

    private async Task SearchAsync(string query)
    {
        var token = ++_searchToken;
        IsSearchingMarket = true;
        Notice = null;

        try
        {
            var items = await _host.Plugins.SearchAsync(query);
            if (token != _searchToken)
            {
                return;
            }

            var installed = InstalledNames();
            _marketTotal = items.Count;
            Market.Clear();
            foreach (var item in items.Take(MaxMarketCards))
            {
                Market.Add(new MarketPluginViewModel(item, installed.Contains(item.Name)));
            }

            OnPropertyChanged(nameof(MarketCountText));
            OnPropertyChanged(nameof(HasMarket));
            OnPropertyChanged(nameof(ShowMarketEmpty));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            if (token == _searchToken)
            {
                Market.Clear();
                _marketTotal = 0;
                ShowNotice($"市场连不上：{ex.Message}。可以直接用「手动安装」填包名或 git 地址。", isError: true);
                OnPropertyChanged(nameof(HasMarket));
                OnPropertyChanged(nameof(ShowMarketEmpty));
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
            Installed.Add(new InstalledPluginViewModel(plugin));
        }

        ManualExtensions.Clear();
        foreach (var extension in _host.Plugins.ListManualExtensions())
        {
            ManualExtensions.Add(extension);
        }

        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(HasManualExtensions));
        OnPropertyChanged(nameof(InstalledCountText));

        // 装完 / 卸完之后市场里的按钮状态要跟着变，不用重新联网。
        var names = InstalledNames();
        foreach (var item in Market)
        {
            item.IsInstalled = names.Contains(item.Name);
        }
    }

    // ---- 手动安装 ----

    [ObservableProperty]
    private bool isManualOpen;

    [ObservableProperty]
    private string manualSource = string.Empty;

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
        if (await InstallAsync(source, null))
        {
            IsManualOpen = false;
            ManualSource = string.Empty;
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
        var progress = new Progress<string>(text => item.Progress = text);
        try
        {
            if (await InstallAsync("npm:" + item.Name, progress))
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
    private async Task<bool> InstallAsync(string source, IProgress<string>? progress)
    {
        if (!await _installGate.WaitAsync(0))
        {
            ShowNotice("已经有一个安装在跑，等它结束再装下一个。", isError: true);
            return false;
        }

        try
        {
            ShowNotice($"正在安装 {source}…", isError: false);
            var result = await _host.Plugins.InstallAsync(source, progress);

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
                $"已安装 {installedName ?? result.Source}，写进 settings.json。"
                + "下一个会话生效——当前这条会话仍用旧的扩展集。",
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
            _installGate.Release();
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

        if (!await _installGate.WaitAsync(0))
        {
            ShowNotice("有一个安装 / 卸载正在进行，等它结束。", isError: true);
            return;
        }

        try
        {
            var result = await _host.Plugins.RemoveAsync(item.Info.Source, item.Info.IsProjectScope);
            if (!result.Ok)
            {
                ShowNotice($"卸载失败：{result.Detail}", isError: true);
                return;
            }

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
            _installGate.Release();
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
