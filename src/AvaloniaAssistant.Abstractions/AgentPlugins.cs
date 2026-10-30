namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 一个配置在 <c>settings.json</c> 的 <c>packages</c> 里的 pi 包。
/// 它可能装着扩展（TS/JS 或 .dll）、技能、提示词、主题——安装完成不代表加载成功，
/// 所以这里只报「配置 + 磁盘上有没有 + 有哪些扩展入口」，不谎报「已启用」。
/// </summary>
public sealed record AgentPluginInfo(
    string Source,
    string Name,
    string Scope,
    bool IsFiltered,
    string? InstalledPath,
    IReadOnlyList<string> Extensions)
{
    /// <summary>用户级 / 项目级，界面直接显示。</summary>
    public bool IsProjectScope => string.Equals(Scope, "project", StringComparison.OrdinalIgnoreCase);

    public bool IsInstalled => !string.IsNullOrEmpty(InstalledPath);
}

/// <summary>
/// 不经过包管理器、直接放在扩展目录里的扩展文件（1.4.5 起 <c>.ts</c> / <c>.js</c> 也能直接加载）。
/// </summary>
public sealed record AgentExtensionInfo(string Name, string Path, bool IsSource);

/// <summary>
/// 市场里的一个包。数据来自 npm registry 的 <c>pi-package</c> 关键词，
/// 和 pi 官方 gallery 同一份来源。
/// </summary>
public sealed record AgentPluginMarketItem(
    string Name,
    string Version,
    string Description,
    string Publisher,
    string PublishedAt,
    IReadOnlyList<string> Keywords,
    string Url);

/// <summary>安装 / 卸载的结论。失败要带上原始错误，不能只回一个 false。</summary>
public sealed record AgentPluginInstallResult(bool Ok, string Source, string Detail, string? Path = null)
{
    public static AgentPluginInstallResult Failed(string source, string detail)
        => new(false, source, detail);
}

/// <summary>
/// pi 包的安装与管理。背后是 Pi.CodingAgent 自己的 PackageManager，
/// 所以写进 <c>settings.json</c> 的形态、装到哪个目录、下次会话怎么解析，都和 <c>pi install</c> 一致。
/// </summary>
public interface IPluginStore
{
    /// <summary>用户级 npm 包的安装根目录（<c>~/.pi/agent/npm</c>）。</summary>
    string PackagesRoot { get; }

    /// <summary>手动放置扩展的目录（<c>~/.pi/agent/extensions</c>）。</summary>
    string ExtensionsRoot { get; }

    /// <summary>读 <c>settings.json</c> 里的包清单，并补上磁盘上的实际安装状态。</summary>
    IReadOnlyList<AgentPluginInfo> ListInstalled();

    /// <summary>扩展目录里直接放着的扩展文件。</summary>
    IReadOnlyList<AgentExtensionInfo> ListManualExtensions();

    /// <summary>安装并写进 settings.json。source 形如 <c>npm:pkg@1.0.0</c>、<c>git:github.com/a/b</c> 或本地路径。</summary>
    Task<AgentPluginInstallResult> InstallAsync(
        string source,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>从 settings.json 移除并卸载；项目级包要指定作用域，否则删的是用户级那份。</summary>
    Task<AgentPluginInstallResult> RemoveAsync(
        string source,
        bool projectScope = false,
        CancellationToken cancellationToken = default);

    /// <summary>按关键词搜 npm 上的 pi 包；空关键词返回当前热门的一批。</summary>
    Task<IReadOnlyList<AgentPluginMarketItem>> SearchAsync(
        string? query,
        CancellationToken cancellationToken = default);
}
