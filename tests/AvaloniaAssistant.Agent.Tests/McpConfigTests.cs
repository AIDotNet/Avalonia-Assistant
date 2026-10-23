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

    [Fact]
    public void ImportsStandardBlockAndKeepsEveryField()
    {
        var result = CapabilityInstaller.ImportMcpServers(Config, """
            {
              "mcpServers": {
                "fs": {
                  "command": "node",
                  "args": ["server.js", "--root", "D:/work"],
                  "env": { "TOKEN": "abc" },
                  "cwd": "D:/work"
                },
                "docs": {
                  "type": "http",
                  "url": "https://mcp.example.com/mcp",
                  "headers": { "Authorization": "Bearer t" },
                  "exposure": "tools"
                }
              }
            }
            """);

        Assert.Equal(["fs", "docs"], result.Added);
        Assert.Equal(2, result.Written);

        var servers = ReadServers();
        var fs = servers["fs"]!.AsObject();
        Assert.Equal("direct", fs["exposure"]!.GetValue<string>());
        Assert.Equal("abc", fs["env"]!["TOKEN"]!.GetValue<string>());
        Assert.Equal("D:/work", fs["cwd"]!.GetValue<string>());

        var docs = servers["docs"]!.AsObject();
        Assert.Equal("https://mcp.example.com/mcp", docs["url"]!.GetValue<string>());
        Assert.Equal("Bearer t", docs["headers"]!["Authorization"]!.GetValue<string>());
        // 自己写了 exposure 就尊重它，不覆盖成 direct。
        Assert.Equal("tools", docs["exposure"]!.GetValue<string>());
    }

    [Fact]
    public void ImportWrapsNpmFamilyCommandOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        CapabilityInstaller.ImportMcpServers(Config, """
            { "mcpServers": { "fs": { "command": "npx", "args": ["-y", "pkg"] } } }
            """);

        var fs = ReadServers()["fs"]!.AsObject();
        Assert.Equal("cmd", fs["command"]!.GetValue<string>());
        Assert.Equal(
            ["/c", "npx", "-y", "pkg"],
            fs["args"]!.AsArray().Select(node => node!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void ImportAcceptsVsCodeAndBareMapShapes()
    {
        CapabilityInstaller.ImportMcpServers(Config, """
            { "servers": { "a": { "type": "stdio", "command": "node" } } }
            """);
        CapabilityInstaller.ImportMcpServers(Config, """
            { "mcp": { "servers": { "b": { "url": "https://x/mcp" } } } }
            """);
        CapabilityInstaller.ImportMcpServers(Config, """
            { "c": { "command": "node" } }
            """);

        var servers = ReadServers();
        Assert.Equal("node", servers["a"]!["command"]!.GetValue<string>());
        Assert.Equal("https://x/mcp", servers["b"]!["url"]!.GetValue<string>());
        Assert.Equal("direct", servers["c"]!["exposure"]!.GetValue<string>());
    }

    [Fact]
    public void ImportSkipsDuplicatesUnlessOverwriteIsOn()
    {
        CapabilityInstaller.AddMcpServer(Config, "fs", "node", "old.js");

        var skipped = CapabilityInstaller.ImportMcpServers(
            Config, """{ "mcpServers": { "fs": { "command": "node", "args": ["new.js"] } } }""");

        Assert.Equal(["fs"], skipped.Skipped);
        Assert.Equal(0, skipped.Written);
        Assert.Equal(["old.js"], ReadServers()["fs"]!["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());

        var replaced = CapabilityInstaller.ImportMcpServers(
            Config, """{ "mcpServers": { "fs": { "command": "node", "args": ["new.js"] } } }""", overwrite: true);

        Assert.Equal(["fs"], replaced.Replaced);
        Assert.Equal(["new.js"], ReadServers()["fs"]!["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void ImportRejectsBrokenInput()
    {
        Assert.Contains("不是合法 JSON", Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.ImportMcpServers(Config, "{ nope ")).Message);

        Assert.Contains("没有服务端", Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.ImportMcpServers(Config, """{ "mcpServers": {} }""")).Message);

        Assert.Contains("command", Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.ImportMcpServers(Config, """{ "mcpServers": { "x": { "env": {} } } }""")).Message);

        Assert.Contains("名称", Assert.Throws<InvalidOperationException>(
            () => CapabilityInstaller.ImportMcpServers(Config, """{ "mcpServers": { "bad name": { "command": "node" } } }""")).Message);

        // 出错时一个字节都不该落盘。
        Assert.False(File.Exists(Config));
    }

    [Fact]
    public void ImportToleratesTrailingCommasAndComments()
    {
        CapabilityInstaller.ImportMcpServers(Config, """
            {
              // 从文档里复制过来的常见写法
              "mcpServers": {
                "fs": { "command": "node", "args": ["a.js"], },
              },
            }
            """);

        Assert.Equal(["a.js"], ReadServers()["fs"]!["args"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
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
