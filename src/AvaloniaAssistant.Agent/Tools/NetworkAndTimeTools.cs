using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent.Tools;

internal sealed partial class FetchUrlTool : IAgentTool
{
    private static readonly HttpClient Client = CreateClient();

    public AgentToolInfo Info { get; } = new(
        "fetch_url",
        "读取网页",
        "抓取一个网页并返回正文文本（已去掉脚本和样式）。适合总结文档、核对资料。",
        "网络",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo("url", "string", "http 或 https 地址", true),
            new ToolParameterInfo("max_chars", "integer", "最多返回的字符数，默认 12000", false),
        ]);

    public async Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var url = JsonArgs.RequiredString(invocation.Json, "url");
        var maxChars = Math.Clamp(JsonArgs.OptionalInt(invocation.Json, "max_chars", 12_000), 1000, 50_000);

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("只支持 http/https 地址。");
        }

        using var response = await Client.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
        {
            var title = TitleRegex().Match(body).Groups[1].Value.Trim();
            var text = ToPlainText(body);
            var header = string.IsNullOrWhiteSpace(title) ? $"网页：{uri}\n\n" : $"网页：{title}\n地址：{uri}\n\n";
            return header + ToolText.Cap(text, maxChars);
        }

        return $"地址：{uri}\n类型：{contentType}\n\n" + ToolText.Cap(body, maxChars);
    }

    private static string ToPlainText(string html)
    {
        var cleaned = ScriptStyleRegex().Replace(html, " ");
        cleaned = BlockBreakRegex().Replace(cleaned, "\n");
        cleaned = TagRegex().Replace(cleaned, " ");
        cleaned = WebUtility.HtmlDecode(cleaned);
        cleaned = WhitespaceRegex().Replace(cleaned, " ");
        cleaned = BlankLineRegex().Replace(cleaned, "\n\n");
        return cleaned.Trim();
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(25) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("AvaloniaAssistant", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return client;
    }

    [GeneratedRegex("<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex("<(script|style|noscript)[^>]*>.*?</\\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex("<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockBreakRegex();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t\f\v]+")]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"\n\s*\n\s*\n+")]
    private static partial Regex BlankLineRegex();
}

internal sealed class OpenUrlTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "open_url",
        "在浏览器打开",
        "用系统默认浏览器打开一个网页。",
        "网络",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo("url", "string", "http 或 https 地址", true),
        ]);

    public Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var url = JsonArgs.RequiredString(invocation.Json, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException("只支持 http/https 地址。");
        }

        Shell.Open(uri.ToString());
        return Task.FromResult($"已在浏览器打开：{uri}");
    }
}

internal sealed class CurrentTimeTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "current_time",
        "查看当前时间",
        "返回本机当前日期、时间和时区。安排日程或计算时间差时使用。",
        "系统",
        RequiresApproval: false,
        Parameters: []);

    public Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.Now;
        return Task.FromResult(
            $"当前时间：{now:yyyy-MM-dd HH:mm:ss}\n星期：{now:dddd}\n时区：{TimeZoneInfo.Local.DisplayName}（UTC{now.Offset:hh\\:mm}）");
    }
}
