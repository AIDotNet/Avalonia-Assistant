using System.Net;
using System.Net.Sockets;
using System.Text;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Agent;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

public sealed class OpenAiWireTests
{
    [Fact]
    public async Task StreamsTextThroughOpenAiCompatibleEndpoint()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var server = ServeOnceAsync(listener);

        var settings = new WireSettings
        {
            Provider = AgentProviderKind.DeepSeek,
            ApiKey = "test-key",
            BaseUrl = $"http://127.0.0.1:{port}/v1",
            Model = "test-model",
        };

        var runtime = new PiAgentRuntime(settings);
        var request = new AgentRunRequest(
            "wire-test",
            "打个招呼",
            [],
            PermissionMode.ReadOnly,
            Path.GetTempPath(),
            null);

        var events = new List<AgentStreamEvent>();
        await foreach (var @event in runtime.RunAsync(request))
        {
            events.Add(@event);
        }

        var body = await server;
        var text = string.Concat(events.OfType<AgentStreamEvent.TextDelta>().Select(item => item.Delta));

        Assert.Contains("你好，世界", text);
        Assert.Contains(events, item => item is AgentStreamEvent.RunCompleted { StopReason: AgentStopReason.Completed });
        Assert.Contains("current_time", body);
        Assert.Contains("test-model", body);
    }

    private static async Task<string> ServeOnceAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        await using var stream = client.GetStream();

        var requestText = await ReadRequestAsync(stream);
        var split = requestText.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var body = split >= 0 ? requestText[(split + 4)..] : string.Empty;

        const string payload = """
            data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"role":"assistant","content":"你好"},"finish_reason":null}]}

            data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{"content":"，世界"},"finish_reason":null}]}

            data: {"id":"chatcmpl-test","object":"chat.completion.chunk","created":1,"model":"test-model","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

            data: [DONE]

            """;

        var response = Encoding.UTF8.GetBytes(
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: text/event-stream\r\n" +
            "Cache-Control: no-cache\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            payload.Replace("\n", "\r\n"));

        await stream.WriteAsync(response);
        await stream.FlushAsync();
        return body;
    }

    private static async Task<string> ReadRequestAsync(NetworkStream stream)
    {
        var builder = new StringBuilder();
        var buffer = new byte[8192];
        var headerEnd = -1;
        var contentLength = 0;

        while (true)
        {
            var read = await stream.ReadAsync(buffer);
            if (read == 0)
            {
                break;
            }

            builder.Append(Encoding.UTF8.GetString(buffer, 0, read));
            var text = builder.ToString();

            if (headerEnd < 0)
            {
                headerEnd = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                if (headerEnd >= 0)
                {
                    foreach (var line in text[..headerEnd].Split("\r\n"))
                    {
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            int.TryParse(line[15..].Trim(), out contentLength);
                        }
                    }
                }
            }

            if (headerEnd >= 0 && text.Length - (headerEnd + 4) >= contentLength)
            {
                break;
            }
        }

        return builder.ToString();
    }

    private sealed class WireSettings : ISettingsProvider
    {
        public AssistantSettings Current { get; } = new();

        public AgentProviderKind Provider
        {
            init => Current.Provider = value;
        }

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
