using System.Text.Json.Nodes;
using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>mcp.json 的写入：保留原内容、拒绝重名、Windows 上补 <c>cmd /c</c> 垫片。</summary>
public sealed class McpConfigTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ava-mcp-" + Guid.NewGuid().ToString("n"));

    public McpConfigTests() => Directory.CreateDirectory(_root);

    private string Config => Path.Combine(_root, "mcp.json");

    [Fact]
    public void WritesServerWithDirectExposureAndArgs()
    {
        CapabilityInstaller.AddMcpServer(Config, "filesystem", "node", "server.js --root D:/work");

        var server = ReadServers()["filesystem"]!.AsObject();

        Assert.Equal("direct", server["exposure"]!.GetValue<string>());
        Assert.Equal("node", server["command"]!.GetValue<string>());
        Assert.Equal(
            ["server.js", "--root", "D:/work"],
            server["args"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void WrapsNpmFamilyCommandWithCmdOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CapabilityInstaller.AddMcpServer(
            Config, "filesystem", "npx", "-y @modelcontextprotocol/server-filesystem D:/work");

        var server = ReadServers()["filesystem"]!.AsObject();
        Assert.Equal("cmd", server["command"]!.GetValue<string>());
        Assert.Equal(
            ["/c", "npx", "-y", "@modelcontextprotocol/server-filesystem", "D:/work"],
            server["args"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void KeepsExistingContent()
    {
        File.WriteAllText(Config, """
            {
              "mcpServers": { "old": { "command": "node" } },
              "note": "别动我"
            }
            """);

        CapabilityInstaller.AddMcpServer(Config, "second", "node", "b.js");

        var root = JsonNode.Parse(File.ReadAllText(Config))!.AsObject();
        Assert.Equal("node", root["mcpServers"]!["old"]!["command"]!.GetValue<string>());
        Assert.NotNull(root["mcpServers"]!["second"]);
        Assert.Equal("别动我", root["note"]!.GetValue<string>());
    }

    [Fact]
    public void RejectsDuplicateAndInvalidName()
    {
        CapabilityInstaller.AddMcpServer(Config, "demo", "node", "");

        Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.AddMcpServer(Config, "demo", "node", ""));
        Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.AddMcpServer(Config, "bad name!", "node", ""));
        Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.AddMcpServer(Config, "ok", "  ", ""));
    }

    [Fact]
    public void RemovesOnlyTheNamedServer()
    {
        CapabilityInstaller.AddMcpServer(Config, "one", "node", "");
        CapabilityInstaller.AddMcpServer(Config, "two", "node", "");

        Assert.True(CapabilityInstaller.RemoveMcpServer(Config, "one"));
        Assert.False(CapabilityInstaller.RemoveMcpServer(Config, "missing"));

        var servers = ReadServers();
        Assert.Null(servers["one"]);
        Assert.NotNull(servers["two"]);
    }

    private JsonObject ReadServers()
        => JsonNode.Parse(File.ReadAllText(Config))!["mcpServers"]!.AsObject();

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
