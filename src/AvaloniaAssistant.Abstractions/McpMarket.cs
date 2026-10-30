namespace AvaloniaAssistant.Abstractions;

/// <summary>MCP 市场里的一个服务端。<see cref="Source"/> 决定它能不能一键装。</summary>
public sealed record McpMarketItem(
    string Id,
    string Name,
    string Description,
    string Category,
    int Views,
    string Source,
    string Url)
{
    /// <summary>只有 ModelScope 的条目带得回完整 server_config，可直接写进 mcp.json。</summary>
    public bool CanInstall => string.Equals(Source, McpSources.ModelScope, StringComparison.Ordinal);

    public string ViewsText => Views switch
    {
        >= 10_000 => $"{Views / 10_000d:0.#}万",
        >= 1_000 => $"{Views / 1_000d:0.#}K",
        _ => Views.ToString(),
    };
}

public static class McpSources
{
    public const string ModelScope = "modelscope";

    public const string Directory = "mcp.so";
}

/// <summary>一页市场结果。<see cref="Total"/> 只有 ModelScope 给得出，mcp.so 用它自己的条数。</summary>
public sealed record McpMarketPage(IReadOnlyList<McpMarketItem> Items, int Total)
{
    public static readonly McpMarketPage Empty = new([], 0);
}

/// <summary>装一个 MCP 服务端的结果。</summary>
public sealed record McpInstallResult(bool Success, string Message);

/// <summary>
/// MCP 市场。两个来源的能力差别是客观存在的，接口不硬凑：
/// <list type="bullet">
/// <item><b>ModelScope</b>：<c>PUT /openapi/v1/mcp/servers</c> 有分页和搜索，详情接口返回
/// <c>server_config</c>（<c>{"mcpServers":{…}}</c>），能一键写入配置；</item>
/// <item><b>mcp.so</b>：没有公开接口，只能抓 <c>/servers</c> 页的 SSR 列表，详情页对多数条目
/// 明确写着「No standard config provided」。所以它的条目只做浏览，点了在浏览器里看。</item>
/// </list>
/// </summary>
public interface IMcpMarket
{
    Task<McpMarketPage> SearchAsync(
        string query,
        int page = 1,
        CancellationToken cancellationToken = default);

    /// <summary>mcp.so 的热门列表。没有搜索参数可用，传什么都没有意义。</summary>
    Task<McpMarketPage> BrowseDirectoryAsync(CancellationToken cancellationToken = default);

    /// <summary>取回配置并合并进 <paramref name="configPath"/>，同名条目已存在时不覆盖。</summary>
    Task<McpInstallResult> InstallAsync(
        McpMarketItem item,
        string configPath,
        CancellationToken cancellationToken = default);
}
