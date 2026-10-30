using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// MCP 市场的两个来源。ModelScope 有完整接口，mcp.so 只能抓页面——两者的差异写在
/// <see cref="IMcpMarket"/> 的注释里，这里不粉饰。
/// </summary>
public sealed partial class McpMarketStore : IMcpMarket
{
    private const string ModelScopeEndpoint = "https://www.modelscope.cn/openapi/v1/mcp/servers";
    private const string DirectoryListUrl = "https://mcp.so/servers";
    private const string DirectoryBase = "https://mcp.so";

    /// <summary>一页 20 条，和 ModelScope 的默认页大小一致。</summary>
    private const int PageSize = 20;

    private readonly HttpClient _http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AvaloniaAssistant/1.0 (mcp market)");
        return http;
    }

    public async Task<McpMarketPage> SearchAsync(
        string query,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        var body = new JsonObject
        {
            ["page_number"] = Math.Max(1, page),
            ["page_size"] = PageSize,
        };

        // 空字符串会被当成一个「搜空词」的查询，返回 0 条；没关键词就不带这个字段。
        var keyword = (query ?? string.Empty).Trim();
        if (keyword.Length > 0)
        {
            body["search"] = keyword;
        }

        using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http
            .PutAsync(ModelScopeEndpoint, content, cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            return McpMarketPage.Empty;
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var root = await JsonNode
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var list = root?["data"]?["mcp_server_list"] as JsonArray;
        if (list is null)
        {
            return McpMarketPage.Empty;
        }

        var items = new List<McpMarketItem>(list.Count);
        foreach (var entry in list)
        {
            var id = ReadString(entry?["id"]);
            if (id.Length == 0)
            {
                continue;
            }

            var name = ReadString(entry?["chinese_name"]);
            if (name.Length == 0)
            {
                name = ReadString(entry?["name"]);
            }

            var categories = entry?["categories"] as JsonArray;
            var category = categories is { Count: > 0 }
                ? ReadString(categories[0])
                : string.Empty;

            items.Add(new McpMarketItem(
                id,
                name.Length == 0 ? id : name,
                ReadString(entry?["description"]),
                category,
                entry?["view_count"]?.GetValue<int>() ?? 0,
                McpSources.ModelScope,
                $"{DirectoryBase}/?s={Uri.EscapeDataString(id)}"));
        }

        var total = root?["data"]?["total_count"]?.GetValue<int>() ?? items.Count;
        return new McpMarketPage(items, total);
    }

    /// <summary>
    /// mcp.so 的列表是服务端渲染的，卡片是 <c>&lt;a href="/servers/&lt;slug&gt;" class="group …"&gt;</c>。
    /// 页面没有搜索参数（<c>?q=</c> / <c>?search=</c> 都返回同一批 30 条），所以这里只能取热门。
    /// </summary>
    public async Task<McpMarketPage> BrowseDirectoryAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var html = await _http.GetStringAsync(DirectoryListUrl, cancellationToken)
                .ConfigureAwait(false);

            var items = new List<McpMarketItem>();
            foreach (Match card in DirectoryCardPattern().Matches(html))
            {
                var slug = card.Groups["slug"].Value;
                var inner = card.Groups["body"].Value;

                var name = WebUtility.HtmlDecode(StripTags(TitlePattern().Match(inner) is { Success: true } title
                    ? title.Groups[1].Value
                    : slug)).Trim();

                var description = WebUtility.HtmlDecode(StripTags(
                    DescriptionPattern().Match(inner) is { Success: true } desc
                        ? desc.Groups[1].Value
                        : string.Empty)).Trim();

                if (slug.Length == 0)
                {
                    continue;
                }

                items.Add(new McpMarketItem(
                    slug,
                    name.Length == 0 ? slug : name,
                    description,
                    string.Empty,
                    0,
                    McpSources.Directory,
                    $"{DirectoryBase}/servers/{slug}"));
            }

            return new McpMarketPage(items, items.Count);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            return McpMarketPage.Empty;
        }
    }

    public async Task<McpInstallResult> InstallAsync(
        McpMarketItem item,
        string configPath,
        CancellationToken cancellationToken = default)
    {
        if (!item.CanInstall)
        {
            return new McpInstallResult(
                false,
                "mcp.so 上的条目没有可解析的配置，得去仓库看安装说明。");
        }

        if (string.IsNullOrWhiteSpace(configPath))
        {
            return new McpInstallResult(false, "mcp.json 路径不可用，先选一个工作区。");
        }

        try
        {
            var url = $"{ModelScopeEndpoint}/{item.Id}";
            var json = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            var root = JsonNode.Parse(json);
            var configs = root?["data"]?["server_config"] as JsonArray;

            if (configs is null || configs.Count == 0)
            {
                return new McpInstallResult(false, "这个服务端没有提供 server_config，只能自己写配置。");
            }

            // server_config 是数组，元素形如 {"mcpServers": {"fetch": {...}}}。
            // 取第一段就够：多段的情况极罕见，硬合并反而容易写出重复键。
            var payload = configs[0]?.ToJsonString();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return new McpInstallResult(false, "server_config 是空的。");
            }

            var result = CapabilityInstaller.ImportMcpServers(configPath, payload, overwrite: false);
            var written = result.Added.Concat(result.Replaced).ToList();
            if (written.Count == 0)
            {
                return new McpInstallResult(
                    false,
                    result.Skipped.Count > 0
                        ? $"{string.Join("、", result.Skipped)} 已经在 mcp.json 里了"
                        : "没有写入任何条目，检查一下返回的配置。");
            }

            return new McpInstallResult(true, $"已写进 mcp.json：{string.Join("、", written)}");
        }
        catch (Exception exception)
        {
            return new McpInstallResult(false, "装不上：" + exception.Message);
        }
    }

    private static string StripTags(string value)
        => TagPattern().Replace(value, string.Empty).Trim();

    private static string ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    [GeneratedRegex(
        "<a\\s+href=\"/servers/(?<slug>[^\"]+)\"[^>]*class=\"group[^\"]*\"[^>]*>(?<body>.*?)</a>",
        RegexOptions.Singleline)]
    private static partial Regex DirectoryCardPattern();

    [GeneratedRegex("<h3[^>]*>(.*?)</h3>", RegexOptions.Singleline)]
    private static partial Regex TitlePattern();

    [GeneratedRegex("line-clamp-2[^\"]*\"[^>]*>(.*?)</(?:p|div|span)>", RegexOptions.Singleline)]
    private static partial Regex DescriptionPattern();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagPattern();
}
