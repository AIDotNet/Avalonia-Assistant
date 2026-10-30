using AvaloniaAssistant.Abstractions;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 插件带的**工具**必须真的进会话：扩展加载成功、命令注册成功，不等于模型能调用它的工具。
/// 会话创建时传的是显式 allowlist，不在名单里的工具会被整段过滤掉——
/// 这个用例钉住的正是「装完插件，模型却看不见工具」这个坑。
/// </summary>
public sealed class PluginToolActivationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ava-plugintool-" + Guid.NewGuid().ToString("n"));

    public PluginToolActivationTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言。
        }
    }

    [Fact]
    public async Task ExtensionRegisteredToolIsVisibleToTheSession()
    {
        var workspace = Path.Combine(_root, "workspace");
        var package = Path.Combine(_root, "plugin");
        Directory.CreateDirectory(Path.Combine(workspace, ".pi"));
        Directory.CreateDirectory(package);

        File.WriteAllText(Path.Combine(package, "package.json"), """
            { "name": "tool-fixture", "pi": { "extensions": ["./index.ts"] } }
            """);

        File.WriteAllText(Path.Combine(package, "index.ts"), """
            import { Type } from "@earendil-works/pi-ai";

            export default function (pi: any) {
              pi.registerCommand("tool-fixture-hello", { description: "x", handler: async () => {} });
              pi.registerTool({
                name: "fixture_tool",
                label: "Fixture tool",
                description: "测试用工具",
                parameters: Type.Object({ q: Type.String() }),
                execute: async () => ({ content: [{ type: "text", text: "ok" }] })
              });
            }
            """);

        File.WriteAllText(
            Path.Combine(workspace, ".pi", "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new { packages = new[] { package.Replace('\\', '/') } }));

        var session = await CodingSessionFactory.CreateAsync(
            new TestSettings().Current, workspace, [], null, CancellationToken.None);

        Assert.Contains("fixture_tool", session.GetAllTools().Select(tool => tool.Name));
        Assert.Contains("fixture_tool", session.GetActiveToolNames());
        Assert.Contains("fixture_tool", session.GetCallableToolNames());

        // 只补插件工具，不要把内置扩展的工具（codemode / tool_search）一起放给模型
        Assert.DoesNotContain("codemode", session.GetActiveToolNames());
        Assert.DoesNotContain("tool_search", session.GetActiveToolNames());
    }

    [Theory]
    [InlineData("read")]
    [InlineData("grep")]
    [InlineData("find")]
    [InlineData("ls")]
    public void ReadOnlyBuiltinsStaySilent(string name)
    {
        Assert.False(ToolAccess.IsMutating(name, null));
        Assert.False(ToolAccess.RequiresApproval(name, null));
    }

    [Theory]
    [InlineData("bash")]
    [InlineData("edit")]
    [InlineData("write")]
    [InlineData("mcp__github__create_issue")]
    [InlineData("some_plugin_tool")]
    public void UnknownOrWritingToolsNeedApproval(string name)
    {
        Assert.True(ToolAccess.IsMutating(name, null));
        Assert.True(ToolAccess.RequiresApproval(name, null));
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
            TrustWorkspace = true,
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
