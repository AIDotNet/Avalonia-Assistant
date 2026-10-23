using AvaloniaAssistant.Abstractions;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// CodingAgent 装配层的行为：技能发现、内置工具、自建工具的激活集合。
/// 这些用例不联网，创建会话后不发起模型请求。
/// </summary>
public sealed class CodingSessionTests
{
    [Fact]
    public async Task DiscoversProjectSkillsAndExposesThemInSystemPrompt()
    {
        using var workspace = new TempWorkspace();
        workspace.WriteSkill("hello", "打招呼的固定流程", "# 打招呼\n先问好，再自我介绍。");

        var session = await CodingSessionFactory.CreateAsync(
            new TestSettings().Current, workspace.Path, [], null, CancellationToken.None);

        Assert.Contains(session.ResourceLoader.Skills, skill => skill.Name == "hello");
        Assert.Contains("hello", session.SystemPrompt);
        Assert.Contains("打招呼的固定流程", session.SystemPrompt);
    }

    [Fact]
    public async Task RegistersBuiltinAndCustomToolsInOneActivationSet()
    {
        using var workspace = new TempWorkspace();
        var registry = new AgentToolRegistry();
        var request = new AgentRunRequest(
            "session-1",
            "hi",
            [],
            "office",
            PermissionMode.Workspace,
            workspace.Path,
            null);

        var tools = registry.CreateCustomTools(request, [], out var catalog);
        var session = await CodingSessionFactory.CreateAsync(
            new TestSettings().Current, workspace.Path, tools, null, CancellationToken.None);

        var callable = session.GetCallableToolNames();

        Assert.Contains("write_file", callable);
        Assert.Contains("read", callable);
        Assert.Contains("grep", callable);
        Assert.Contains("edit", callable);
        Assert.Equal(tools.Count, catalog.Count);
    }

    [Fact]
    public async Task ReadOnlySessionDropsMutatingBuiltinTools()
    {
        using var workspace = new TempWorkspace();
        var settings = new TestSettings().Current;
        settings.Permission = PermissionMode.ReadOnly;

        var session = await CodingSessionFactory.CreateAsync(
            settings, workspace.Path, [], null, CancellationToken.None);

        var callable = session.GetCallableToolNames();

        Assert.Contains("read", callable);
        Assert.Contains("grep", callable);
        Assert.DoesNotContain("edit", callable);
        Assert.DoesNotContain("write", callable);
        Assert.DoesNotContain("bash", callable);
    }

    [Fact]
    public void CapabilityScanPointsAtPiDirectories()
    {
        using var workspace = new TempWorkspace();
        var settings = new TestSettings().Current;
        settings.WorkspacePath = workspace.Path;

        var capabilities = CapabilityScanner.Scan(settings);

        Assert.Equal(Path.Combine(workspace.Path, ".pi", "skills"), capabilities.SkillsRoot);
        Assert.Equal(Path.Combine(workspace.Path, ".pi", "mcp.json"), capabilities.McpConfigPath);
    }

    /// <summary>
    /// MCP 工具从注册到真实调用、再到结果回传的整条链路，包含带参数字段的那条分支。
    /// </summary>
    /// <remarks>
    /// 参数必须带值：Pi.CodingAgent 1.4.1 的 <c>McpServerConnection.ToArguments</c> 把 Agent
    /// 传下来的 <see cref="System.Text.Json.Nodes.JsonNode"/> 直接挂到新 JsonObject 上，
    /// 而 <c>Validation.ValidateToolArguments</c> 返回的子节点已带父节点，于是抛
    /// <c>The node already has a parent.</c>，所有带参数的 MCP 工具都调不动。
    /// 该缺陷在 pi.net 修复（<c>node.DeepClone()</c>）并随 1.4.2 发布，
    /// 上游回归用例是 <c>CallToolAcceptsArgumentValuesThatAlreadyHaveAParent</c>。
    /// 本用例保持带参数，避免以后升级时又退回「只测无参」而漏掉这条路径。
    /// </remarks>
    [Fact]
    public async Task CallsMcpToolEndToEndAndGetsItsResult()
    {
        if (!NodeAvailable())
        {
            return;
        }

        using var workspace = new TempWorkspace();
        var script = Path.Combine(workspace.Path, "mcp-demo.js");
        File.WriteAllText(script, DemoToolServer);
        workspace.WriteMcpConfig("demo", script);

        var stream = Pi.Ai.Providers.Faux.CreateStreamFn(
        [
            Pi.Ai.Providers.Faux.Response.ToolUse(
                new Pi.Ai.Types.ToolCallContent("call-1", "mcp__demo__echo", new Dictionary<string, object?> { ["text"] = "hello" })),
            Pi.Ai.Providers.Faux.Response.Text("工具已返回。"),
        ]);

        var session = await CodingSessionFactory.CreateAsync(
            new TestSettings().Current, workspace.Path, [], stream, CancellationToken.None);

        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline
               && !session.GetCallableToolNames().Contains("mcp__demo__echo", StringComparer.Ordinal))
        {
            await Task.Delay(150);
        }

        var results = new List<Pi.Agent.Types.AgentToolResult>();
        using var subscription = session.Agent.Subscribe((@event, _) =>
        {
            if (@event is Pi.Agent.Types.AgentEvent.ToolExecutionEnd { Result: Pi.Agent.Types.AgentToolResult result })
            {
                results.Add(result);
            }

            return Task.CompletedTask;
        });

        await session.PromptAsync("用 demo 服务端的 echo 说 hello");

        var single = Assert.Single(results);
        var text = string.Concat(single.Content.OfType<Pi.Ai.Types.TextContent>().Select(block => block.Text));
        Assert.False(single.IsError, text);
        Assert.Contains("echo: hello", text);
    }

    [Fact]
    public async Task ConnectsMcpServerDeclaredInProjectConfig()
    {
        if (!NodeAvailable())
        {
            return;
        }

        using var workspace = new TempWorkspace();
        var script = Path.Combine(workspace.Path, "mcp-demo.js");
        File.WriteAllText(script, DemoToolServer);
        workspace.WriteMcpConfig("demo", script);

        var session = await CodingSessionFactory.CreateAsync(
            new TestSettings().Current, workspace.Path, [], null, CancellationToken.None);

        // MCP 连接是异步的：会话创建返回时服务端可能还在握手。真实对话里第一次 prompt 会等它，
        // 这里没有 prompt，所以自己轮询到超时。
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline
               && !session.GetCallableToolNames().Contains("mcp__demo__echo", StringComparer.Ordinal))
        {
            await Task.Delay(150);
        }

        Assert.Contains("mcp__demo__echo", session.GetCallableToolNames());
    }

    private static bool NodeAvailable()
    {
        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo("node", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = System.Diagnostics.Process.Start(startInfo);
            return process is not null && process.WaitForExit(5000);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>最小的 MCP stdio 服务端：应答 initialize / tools/list / tools/call。</summary>
    private const string DemoToolServer = """
        const readline = require('readline');
        const rl = readline.createInterface({ input: process.stdin });
        const send = (message) => process.stdout.write(JSON.stringify(message) + '\n');

        rl.on('line', (line) => {
          let message;
          try { message = JSON.parse(line); } catch { return; }
          if (message.method === 'initialize') {
            send({ jsonrpc: '2.0', id: message.id, result: {
              protocolVersion: '2024-11-05',
              capabilities: { tools: {} },
              serverInfo: { name: 'demo', version: '1.0.0' }
            }});
          } else if (message.method === 'tools/list') {
            send({ jsonrpc: '2.0', id: message.id, result: { tools: [{
              name: 'echo',
              description: '回显传入的文本',
              inputSchema: { type: 'object', properties: { text: { type: 'string' } }, required: ['text'] }
            }]}});
          } else if (message.method === 'tools/call') {
            send({ jsonrpc: '2.0', id: message.id, result: {
              content: [{ type: 'text', text: 'echo: ' + (message.params.arguments?.text || '') }]
            }});
          } else if (message.id !== undefined) {
            send({ jsonrpc: '2.0', id: message.id, result: {} });
          }
        });
        """;


    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "avalonia-coding-" + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void WriteSkill(string name, string description, string body)
        {
            var dir = System.IO.Path.Combine(Path, ".pi", "skills", name);
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                System.IO.Path.Combine(dir, "SKILL.md"),
                $"---\nname: {name}\ndescription: {description}\n---\n\n{body}\n");
        }

        public void WriteMcpConfig(string name, string scriptPath)
        {
            var dir = System.IO.Path.Combine(Path, ".pi");
            Directory.CreateDirectory(dir);
            var config = new
            {
                mcpServers = new Dictionary<string, object>
                {
                    [name] = new
                    {
                        command = "node",
                        args = new[] { scriptPath },
                        exposure = "direct",
                    },
                },
            };

            File.WriteAllText(
                System.IO.Path.Combine(dir, "mcp.json"),
                System.Text.Json.JsonSerializer.Serialize(config, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true,
                }));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录清理失败不影响断言。
            }
        }
    }

    private sealed class TestSettings : ISettingsProvider
    {
        public AssistantSettings Current { get; } = new()
        {
            Provider = AgentProviderKind.Custom,
            ApiKey = "test-key",
            BaseUrl = "http://localhost",
            Model = "test-model",
            Permission = PermissionMode.Workspace,
        };

        public event EventHandler? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
