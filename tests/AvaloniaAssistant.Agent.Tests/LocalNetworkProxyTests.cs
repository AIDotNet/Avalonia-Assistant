using System.Net;
using System.Net.Sockets;
using System.Text;
using AvaloniaAssistant.Abstractions;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 本机 / 内网地址不该被系统代理接管。这里既验判断规则，
/// 也用一个真的本地服务跑一遍探测，确认请求确实直连到了 127.0.0.1。
/// </summary>
public sealed class LocalNetworkProxyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:11434/v1", true)]
    [InlineData("http://localhost:8080/v1", true)]
    [InlineData("http://[::1]:11434/v1", true)]
    [InlineData("http://10.136.2.166:8000/v1", true)]
    [InlineData("http://192.168.1.20/v1", true)]
    [InlineData("http://172.16.0.5/v1", true)]
    [InlineData("http://172.31.255.1/v1", true)]
    [InlineData("http://ollama:11434/v1", true)]
    [InlineData("http://nas.local/v1", true)]
    [InlineData("http://172.32.0.1/v1", false)]
    [InlineData("http://8.8.8.8/v1", false)]
    [InlineData("https://api.deepseek.com/v1", false)]
    [InlineData("https://api.openai.com/v1", false)]
    public void LocalAndPrivateHostsBypassTheProxy(string url, bool expected)
    {
        Assert.Equal(expected, LocalNetworkProxy.ShouldBypass(new Uri(url)));
    }

    [Fact]
    public async Task ProbeReachesALocalServerDirectly()
    {
        // 测试进程本身也可能带着 http_proxy（CI / 沙箱）。不先装这层，下面的请求会被代理吞掉。
        LocalNetworkProxy.Apply();

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var serving = ServeOnceAsync(listener, "{\"ok\":true}");

        var settings = new AssistantSettings
        {
            Provider = AgentProviderKind.Custom,
            ApiKey = "sk-test",
            BaseUrl = $"http://127.0.0.1:{port}/v1",
            Model = "local-model",
            Protocol = AgentProtocol.OpenAiCompatible,
        };

        var result = await ConnectionProbe.TestAsync(settings, CancellationToken.None);
        await serving;

        Assert.True(result.Ok, result.Summary);
    }

    /// <summary>最小 HTTP 服务：读一次请求，回一个 200，然后关掉。</summary>
    private static async Task ServeOnceAsync(TcpListener listener, string body)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();

        var buffer = new byte[8192];
        await stream.ReadAsync(buffer);

        var bytes = Encoding.UTF8.GetBytes(body);
        var head = $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.UTF8.GetBytes(head));
        await stream.WriteAsync(bytes);
        await stream.FlushAsync();
    }
}
