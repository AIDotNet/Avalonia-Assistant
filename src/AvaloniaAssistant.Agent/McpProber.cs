using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 最小 MCP 握手：起进程 → initialize → notifications/initialized → tools/list，然后杀掉。
/// 只为了回答界面上那个问题——「配置写在 mcp.json 里，它到底起没起来」。
/// 不缓存结果、不接管会话连接，失败也不影响用户继续用。
/// </summary>
internal static class McpProber
{
    /// <summary>
    /// 20 秒：本机起一个已缓存的 npx 大约 6 秒；首次要下载包会更久。
    /// 超时不算结论，按钮还在，用户可以重试。
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public static async Task<McpProbeResult> ProbeAsync(
        string configPath,
        string serverName,
        CancellationToken cancellationToken)
    {
        var entry = ReadEntry(configPath, serverName);
        if (entry is null)
        {
            return McpProbeResult.Failed("mcp.json 里找不到这个服务端，可能刚被改过");
        }

        if (entry.Value.TryGetProperty("url", out var url))
        {
            return McpProbeResult.Failed($"HTTP 传输（{url.GetString()}），这一页暂不探测；发一条消息看工具是否出现");
        }

        if (!entry.Value.TryGetProperty("command", out var commandElement)
            || commandElement.GetString() is not { Length: > 0 } command)
        {
            return McpProbeResult.Failed("配置里没有 command，也没有 url");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = command,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        if (entry.Value.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in args.EnumerateArray())
            {
                if (arg.GetString() is { } value)
                {
                    startInfo.ArgumentList.Add(value);
                }
            }
        }

        if (entry.Value.TryGetProperty("cwd", out var cwd) && cwd.GetString() is { Length: > 0 } workingDirectory)
        {
            startInfo.WorkingDirectory = workingDirectory;
        }

        if (entry.Value.TryGetProperty("env", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            foreach (var pair in env.EnumerateObject())
            {
                startInfo.Environment[pair.Name] = pair.Value.GetString() ?? string.Empty;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        var stderr = new StringBuilder();

        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return McpProbeResult.Failed(
                $"进程起不来：{ex.Message}。Windows 上手写 npx / npm 需要写成 cmd /c npx，或改用「导入 JSON」让它自动补。");
        }

        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null)
            {
                return;
            }

            lock (stderr)
            {
                if (stderr.Length < 2000)
                {
                    stderr.AppendLine(e.Data);
                }
            }
        };
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        try
        {
            await SendAsync(process, new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "AvaloniaAssistant", version = "1.0" },
                },
            }, timeout.Token);

            var init = await ReadResponseAsync(process, "1", timeout.Token);
            if (init is null)
            {
                return McpProbeResult.Failed("进程起来了，但没有回 initialize" + Tail(stderr));
            }

            await SendAsync(process, new { jsonrpc = "2.0", method = "notifications/initialized" }, timeout.Token);
            await SendAsync(process, new { jsonrpc = "2.0", id = 2, method = "tools/list", @params = new { } }, timeout.Token);

            var list = await ReadResponseAsync(process, "2", timeout.Token);
            if (list is null)
            {
                return McpProbeResult.Failed("initialize 通了，但 tools/list 没有回包" + Tail(stderr));
            }

            if (list["error"] is { } error)
            {
                return McpProbeResult.Failed("tools/list 报错：" + (error["message"]?.GetValue<string>() ?? error.ToJsonString()));
            }

            var count = list["result"]?["tools"]?.AsArray().Count ?? 0;
            return count switch
            {
                0 => new McpProbeResult(true, 0, "已连接，但没有暴露工具：检查配置里是否写了 exposure: direct"),
                _ => new McpProbeResult(true, count, $"已连接 · {count} 个工具"),
            };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return McpProbeResult.Failed($"20 秒内没握手完成，进程可能卡在启动或首次下载；稍后点「重试」" + Tail(stderr));
        }
        catch (Exception ex)
        {
            return McpProbeResult.Failed(ex.Message + Tail(stderr));
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // 已经退了
            }
        }
    }

    private static async Task SendAsync(Process process, object payload, CancellationToken cancellationToken)
    {
        var line = JsonSerializer.Serialize(payload);
        await process.StandardInput.WriteLineAsync(line.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
    }

    /// <summary>
    /// 逐行读，直到等到 id 对上的那一条。服务端常把日志打到 stdout，
    /// 非 JSON 的行直接跳过，不能因此判定失败。
    /// </summary>
    private static async Task<JsonNode?> ReadResponseAsync(Process process, string id, CancellationToken cancellationToken)
    {
        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                return null;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                var node = JsonNode.Parse(line);
                if (node?["id"]?.ToString() == id)
                {
                    return node;
                }
            }
            catch (JsonException)
            {
                // 不是 JSON：多半是服务端自己的日志
            }
        }
    }

    private static JsonElement? ReadEntry(string configPath, string serverName)
    {
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            if (!document.RootElement.TryGetProperty("mcpServers", out var servers)
                || servers.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var server in servers.EnumerateObject())
            {
                if (string.Equals(server.Name, serverName, StringComparison.Ordinal))
                {
                    // JsonDocument 马上要释放，复制一份出去
                    return server.Value.Clone();
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return null;
        }

        return null;
    }

    private static string Tail(StringBuilder stderr)
    {
        lock (stderr)
        {
            var text = stderr.ToString().Trim();
            if (text.Length == 0)
            {
                return string.Empty;
            }

            var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return "：" + string.Join(" / ", lines.Take(3)).Trim();
        }
    }
}
