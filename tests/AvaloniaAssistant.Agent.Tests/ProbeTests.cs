using System.Net;
using System.Net.Sockets;
using System.Text;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Agent;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 设置页「测试连接」走的就是 <see cref="PiAgentRuntime.ProbeAsync"/>：
/// 这里用本机 TCP 假端点确认它请求的是 /models、带上了 Key、能把模型列表和错误状态码带回来。
/// </summary>
public sealed class ProbeTests
{
    [Fact]
    public async Task ReturnsModelListFromOpenAiCompatibleEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeOnceAsync(
            listener,
            "HTTP/1.1 200 OK",
            """{"object":"list","data":[{"id":"deepseek-flash"},{"id":"deepseek-v4-pro"}]}""");

        var settings = new ProbeSettings
        {
            ApiKey = "test-key",
            BaseUrl = $"http://127.0.0.1:{port}/v1",
            Model = "deepseek-flash",
        };

        var runtime = new PiAgentRuntime(settings);
        var result = await runtime.ProbeAsync(settings.Current);

        var requestText = await server;

        Assert.True(result.Ok, result.Message);
        Assert.Equal(["deepseek-flash", "deepseek-v4-pro"], result.Models);
        Assert.Contains("GET /v1/models", requestText);
        Assert.Contains("Bearer test-key", requestText);
    }

    [Fact]
    public async Task SurfacesHttpStatusWhenKeyIsRejected()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeOnceAsync(
            listener,
            "HTTP/1.1 401 Unauthorized",
            """{"error":{"message":"invalid api key"}}""");

        var settings = new ProbeSettings
        {
            ApiKey = "wrong-key",
            BaseUrl = $"http://127.0.0.1:{port}/v1",
            Model = "deepseek-flash",
        };

        var runtime = new PiAgentRuntime(settings);
        var result = await runtime.ProbeAsync(settings.Current);

        await server;

        Assert.False(result.Ok);
        Assert.Contains("401", result.Message);
        Assert.Contains("invalid api key", result.Message);
    }

    [Fact]
    public async Task RefusesToProbeWithoutApiKey()
    {
        var settings = new ProbeSettings();
        settings.Current.ApiKey = string.Empty;

        var result = await new PiAgentRuntime(settings).ProbeAsync(settings.Current);

        Assert.False(result.Ok);
        Assert.Contains("API Key", result.Message);
    }

    private static async Task<string> ServeOnceAsync(TcpListener listener, string statusLine, string body)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();

        var requestText = await ReadHeadersAsync(stream);
        var payload = Encoding.UTF8.GetBytes(body);
        var head = Encoding.UTF8.GetBytes(
            statusLine + "\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {payload.Length}\r\n" +
            "Connection: close\r\n" +
            "\r\n");

        await stream.WriteAsync(head);
        await stream.WriteAsync(payload);
        await stream.FlushAsync();
        return requestText;
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[4096];

        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            if (builder.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
            {
                break;
            }
        }

        return builder.ToString();
    }

    private sealed class ProbeSettings : ISettingsProvider
    {
        public AssistantSettings Current { get; } = new()
        {
            Provider = AgentProviderKind.DeepSeek,
            ApiKey = "test-key",
            BaseUrl = "http://localhost/v1",
            Model = "deepseek-flash",
        };

        public string ApiKey
        {
            init => Current.ApiKey = value;
        }

        public string BaseUrl
        {
            init => Current.BaseUrl = value;
        }

        public string Model
        {
            init => Current.Model = value;
        }

        public event EventHandler? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
