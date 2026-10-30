using AvaloniaAssistant.Abstractions;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 市场装出来的是**用户级 npm 包**：写在 <c>settings.json</c> 的 <c>packages</c>，
/// 落在 <c>~/.pi/agent/npm/node_modules/&lt;name&gt;</c>。这条路径和项目级本地包不是同一段代码，
/// 单独钉一个用例——否则「装完显示成功、会话里没加载」这种问题测不出来。
/// </summary>
public sealed class UserScopePackageTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ava-userscope-" + Guid.NewGuid().ToString("n"));

    public UserScopePackageTests() => Directory.CreateDirectory(_root);

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
    public async Task UserScopeNpmPackageExtensionLoads()
    {
        var agentDir = Path.Combine(_root, "agent");
        var packageDir = Path.Combine(agentDir, "npm", "node_modules", "fixture-plugin");
        var workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(packageDir);
        Directory.CreateDirectory(workspace);

        // 和真实包（pi-memory / pi-mcp-adapter）同一形状：pi 清单写扩展入口
        File.WriteAllText(Path.Combine(packageDir, "package.json"), """
            {
              "name": "fixture-plugin",
              "version": "1.0.0",
              "keywords": ["pi-package"],
              "pi": { "extensions": ["./index.ts"] }
            }
            """);
        File.WriteAllText(Path.Combine(packageDir, "index.ts"), """
            export default function (pi: any) {
              pi.registerCommand("user-scope-hello", { description: "x", handler: async () => {} });
            }
            """);

        File.WriteAllText(Path.Combine(agentDir, "settings.json"), """
            { "packages": ["npm:fixture-plugin"] }
            """);

        // 直接给 loader 传 agentDir，不碰 PI_CODING_AGENT_DIR：环境变量是全局状态，
        // 改了会和并行跑的其它用例互相干扰。
        var settingsManager = SettingsManager.Create(workspace, agentDir);
        var loader = new DefaultResourceLoader(
            workspace,
            agentDir,
            settingsManager,
            extensionFactories: BuiltInExtensions.All);
        await loader.ReloadAsync(null, CancellationToken.None);

        var loaded = loader.GetExtensions();
        var extension = loaded.Extensions.FirstOrDefault(item => item.Commands.ContainsKey("user-scope-hello"));

        Assert.NotNull(extension);
        Assert.DoesNotContain(
            loaded.Errors,
            error => error.Path.Contains("fixture-plugin", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            loaded.Warnings,
            warning => warning.Path.Contains("fixture-plugin", StringComparison.OrdinalIgnoreCase));

        // 界面上的「扩展入口」也要和真正加载到的一致，不能各说一套
        Assert.Equal(["index.ts"], PluginDiscovery.FindExtensions(packageDir));
    }

    private sealed class TestSettings(string workspace) : ISettingsProvider
    {
        public AssistantSettings Current { get; } = new()
        {
            Provider = AgentProviderKind.Custom,
            ApiKey = "test-key",
            BaseUrl = "http://localhost",
            Model = "test-model",
            Permission = PermissionMode.Workspace,
            TrustWorkspace = true,
            WorkspacePath = workspace,
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
