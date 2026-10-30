using AvaloniaAssistant.Abstractions;
using Pi.Agent;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Xunit;
using Xunit.Abstractions;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 插件的端到端验收：装一个带工具的 pi 包，让（脚本化的）模型真的去调它。
/// <para>
/// 这一层走的是生产路径：<see cref="PiAgentRuntime"/> → <see cref="CodingSessionFactory"/> →
/// 扩展加载（TypeScript 进程内转译）→ allowlist → 工具分发 → 审批回调 → 扩展里的 JS 执行。
/// 只有「模型吐什么 token」是脚本写死的，其余全是真的。
/// </para>
/// </summary>
public sealed class PluginEndToEndTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ava-plugin-e2e-" + Guid.NewGuid().ToString("n"));

    private readonly ITestOutputHelper _output;

    public PluginEndToEndTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(_root);
    }

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
    public async Task PluginToolRunsThroughTheRealRuntime()
    {
        var workspace = Path.Combine(_root, "workspace");
        var package = Path.Combine(_root, "plugin");
        var target = Path.Combine(workspace, "note.txt");
        Directory.CreateDirectory(Path.Combine(workspace, ".pi"));
        Directory.CreateDirectory(package);

        File.WriteAllText(Path.Combine(package, "package.json"), """
            { "name": "e2e-fixture", "version": "1.0.0", "pi": { "extensions": ["./index.ts"] } }
            """);

        // 工具真的会落盘：断言文件存在，才算「用上了」，不是「注册上了」。
        File.WriteAllText(Path.Combine(package, "index.ts"), """
            import { writeFileSync } from "node:fs";

            export default function (pi: any) {
              pi.registerTool({
                name: "fixture_write",
                label: "Fixture write",
                description: "把一个文件写到指定路径",
                parameters: {
                  type: "object",
                  properties: { path: { type: "string" }, text: { type: "string" } },
                  required: ["path", "text"]
                },
                execute: async (_callId: any, params: any) => {
                  writeFileSync(params.path, params.text, "utf8");
                  return { content: [{ type: "text", text: "written:" + params.path }] };
                }
              });
            }
            """);

        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".pi", "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new { packages = new[] { package.Replace('\\', '/') } }));

        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.ToolUse(new ToolCallContent(
                "call-1",
                "fixture_write",
                new { path = target, text = "来自插件工具" })),
            Faux.Response.Text("写好了。"),
        ]);

        var gate = new RecordingGate();
        var settings = new TestSettings(workspace);
        var runtime = new PiAgentRuntime(settings, stream);

        var events = await CollectAsync(runtime, workspace, gate);
        foreach (var @event in events.OfType<AgentStreamEvent.ToolFinished>())
        {
            _output.WriteLine($"tool {(@event.IsError ? "FAILED" : "OK")}: {@event.Result}");
        }

        var finished = Assert.Single(events.OfType<AgentStreamEvent.ToolFinished>());
        Assert.False(finished.IsError, finished.Result);
        Assert.Contains("written:", finished.Result);

        // 扩展里的 JS 真的执行了
        Assert.True(File.Exists(target), finished.Result);
        Assert.Equal("来自插件工具", File.ReadAllText(target));

        // 插件工具没有本地元数据，必须走审批
        var request = Assert.Single(gate.Requests);
        Assert.Equal("fixture_write", request.ToolName);
    }

    [Fact]
    public async Task DisabledPluginToolCannotBeCalled()
    {
        var workspace = Path.Combine(_root, "workspace-off");
        var package = Path.Combine(_root, "plugin-off");
        var target = Path.Combine(workspace, "should-not-exist.txt");
        Directory.CreateDirectory(Path.Combine(workspace, ".pi"));
        Directory.CreateDirectory(package);

        File.WriteAllText(Path.Combine(package, "package.json"), """
            { "name": "e2e-off", "version": "1.0.0", "pi": { "extensions": ["./index.ts"] } }
            """);
        File.WriteAllText(Path.Combine(package, "index.ts"), """
            import { writeFileSync } from "node:fs";
            export default function (pi: any) {
              pi.registerTool({
                name: "fixture_write",
                label: "Fixture write",
                description: "写文件",
                parameters: { type: "object", properties: { path: { type: "string" }, text: { type: "string" } } },
                execute: async (_callId: any, params: any) => {
                  writeFileSync(params.path, params.text, "utf8");
                  return { content: [{ type: "text", text: "written" }] };
                }
              });
            }
            """);

        await File.WriteAllTextAsync(
            Path.Combine(workspace, ".pi", "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new { packages = new[] { package.Replace('\\', '/') } }));

        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.ToolUse(new ToolCallContent(
                "call-1",
                "fixture_write",
                new { path = target, text = "不该落盘" })),
            Faux.Response.Text("工具不可用。"),
        ]);

        var settings = new TestSettings(workspace);
        settings.Current.DisabledTools.Add("fixture_write");
        var runtime = new PiAgentRuntime(settings, stream);

        var events = await CollectAsync(runtime, workspace, new RecordingGate());

        // 工具不在会话的工具集里：调用只会以失败收场，更不该真的写盘
        // （ToolStarted 是模型发起调用时就发的，失败仍会有这一条，不能拿它当「调用了」的证据）
        foreach (var finished in events.OfType<AgentStreamEvent.ToolFinished>())
        {
            _output.WriteLine($"tool {finished.Name} error={finished.IsError}: {finished.Result}");
        }

        Assert.False(File.Exists(target));
        Assert.DoesNotContain(
            events.OfType<AgentStreamEvent.ToolFinished>(),
            item => item.Name == "fixture_write" && !item.IsError);
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(
        IAssistantRuntime runtime,
        string workspace,
        IToolApprovalGate gate)
    {
        var request = new AgentRunRequest(
            Guid.NewGuid().ToString("n"),
            "用插件工具写一个文件",
            [],
            "office",
            PermissionMode.Workspace,
            workspace,
            gate);

        var events = new List<AgentStreamEvent>();
        await foreach (var @event in runtime.RunAsync(request))
        {
            events.Add(@event);
        }

        return events;
    }

    private sealed class RecordingGate : IToolApprovalGate
    {
        public List<ToolApprovalRequest> Requests { get; } = [];

        public Task<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(ToolApprovalDecision.Allow);
        }
    }

    private sealed class ScriptedStreamFactory(IReadOnlyList<Faux.Response> responses) : IPiStreamFactory
    {
        public StreamFn Create(AssistantSettings settings) => Faux.CreateStreamFn(responses);
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
