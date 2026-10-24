using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 设置页「测试连接」的底层实现：用表单里那份设置发一次最小请求。
/// 不走 Agent 会话（那会连带装载技能和 MCP，失败原因就不纯了），
/// 只验证 Key、地址、模型名和协议这四件事能不能通。
/// </summary>
internal static class ConnectionProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task<ConnectionTestResult> TestAsync(
        AssistantSettings settings,
        CancellationToken cancellationToken)
    {
        var apiKey = (settings.ApiKey ?? string.Empty).Trim();
        var baseUrl = (settings.BaseUrl ?? string.Empty).Trim().TrimEnd('/');
        var model = (settings.Model ?? string.Empty).Trim();

        if (apiKey.Length == 0)
        {
            return ConnectionTestResult.Failure(
                "还没填 API Key",
                "把服务商后台的 Key 粘进来；它只保存在本机。",
                0);
        }

        if (model.Length == 0)
        {
            return ConnectionTestResult.Failure(
                "还没填模型",
                "填一个这个端点支持的模型名。",
                0);
        }

        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return ConnectionTestResult.Failure(
                "Base URL 不像一个网址",
                "要以 http:// 或 https:// 开头，常见写法是 https://api.deepseek.com/v1。",
                0);
        }

        var (path, payload, anthropic) = BuildRequest(settings, baseUrl, model);

        using var client = new HttpClient { Timeout = Timeout };
        using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };

        if (anthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
        }

        var watch = Stopwatch.StartNew();
        try
        {
            using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            watch.Stop();

            if (response.IsSuccessStatusCode)
            {
                return ConnectionTestResult.Success(model, watch.ElapsedMilliseconds);
            }

            // 服务端已经回了话，只是不认这次请求：把状态码交给人话分类器，再附上原文摘要
            var info = RunErrorInfo.Classify($"{(int)response.StatusCode} {body}");
            var title = info.Kind == RunErrorKind.Unknown ? "服务端拒绝了这次请求" : info.Title;
            return ConnectionTestResult.Failure(
                title,
                $"{info.Hint}（HTTP {(int)response.StatusCode} · {Excerpt(body)}）",
                watch.ElapsedMilliseconds);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            watch.Stop();
            return ConnectionTestResult.Failure(
                "请求超时",
                "20 秒没有响应：地址可能不对，或者网络 / 代理卡住了。",
                watch.ElapsedMilliseconds);
        }
        catch (HttpRequestException ex)
        {
            watch.Stop();
            var info = RunErrorInfo.Classify(ex.Message);
            return ConnectionTestResult.Failure(
                info.Kind == RunErrorKind.Unknown ? "连不上这个地址" : info.Title,
                $"{info.Hint}（{Excerpt(ex.Message)}）",
                watch.ElapsedMilliseconds);
        }
    }

    /// <summary>按协议拼最小请求：一句话、最少 token，只求服务端回一个 2xx。</summary>
    private static (string Path, string Payload, bool Anthropic) BuildRequest(
        AssistantSettings settings,
        string baseUrl,
        string model)
    {
        switch (settings.EffectiveProtocol)
        {
            case AgentProtocol.AnthropicMessages:
            {
                var path = baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? "/messages" : "/v1/messages";
                var payload = JsonSerializer.Serialize(new
                {
                    model,
                    max_tokens = 8,
                    messages = new[] { new { role = "user", content = "ping" } },
                });
                return (path, payload, true);
            }

            case AgentProtocol.OpenAiResponses:
            {
                var payload = JsonSerializer.Serialize(new
                {
                    model,
                    input = "ping",
                    max_output_tokens = 16,
                });
                return ("/responses", payload, false);
            }

            default:
            {
                var payload = JsonSerializer.Serialize(new
                {
                    model,
                    messages = new[] { new { role = "user", content = "ping" } },
                    max_tokens = 8,
                    stream = false,
                });
                return ("/chat/completions", payload, false);
            }
        }
    }

    /// <summary>错误体可能是 HTML，压成一行再截断，别把整页塞进提示条。</summary>
    private static string Excerpt(string text)
    {
        var flat = (text ?? string.Empty).ReplaceLineEndings(" ").Trim();
        while (flat.Contains("  ", StringComparison.Ordinal))
        {
            flat = flat.Replace("  ", " ", StringComparison.Ordinal);
        }

        return flat.Length <= 160 ? flat : flat[..160] + "…";
    }
}
