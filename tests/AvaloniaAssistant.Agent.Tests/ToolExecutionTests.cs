using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Agent;
using Pi.Agent;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

public sealed class ToolExecutionTests
{
    [Fact]
    public async Task WritesWithCustomToolAndReadsBackWithBuiltin()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "ava-tools", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workspace);

        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.ToolUse(new ToolCallContent("call-1", "write_file", new { path = "hello.txt", content = "hello from workspace" })),
            Faux.Response.ToolUse(new ToolCallContent("call-2", "read", new { path = "hello.txt" })),
            Faux.Response.Text("写好了。"),
        ]);

        var runtime = new PiAgentRuntime(new TestSettings(workspace), stream);
        var events = await CollectAsync(runtime, workspace);

        var results = events.OfType<AgentStreamEvent.ToolFinished>().ToList();
        Assert.Equal(2, results.Count);
        Assert.All(results, item => Assert.False(item.IsError, item.Result));
        Assert.Contains("hello.txt", results[0].Result);
        Assert.Contains("hello from workspace", results[1].Result);

        Directory.Delete(workspace, recursive: true);
    }

    [Fact]
    public async Task BlocksWritesOutsideWorkspaceWithoutFullAccess()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "ava-tools", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workspace);
        var outside = Path.Combine(Path.GetTempPath(), $"ava-outside-{Guid.NewGuid():n}.txt");

        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.ToolUse(new ToolCallContent(
                "call-1",
                "write_file",
                new { path = outside, content = "should not be written" })),
            Faux.Response.Text("没有权限。"),
        ]);

        var runtime = new PiAgentRuntime(new TestSettings(workspace), stream);
        var events = await CollectAsync(runtime, workspace);

        var result = Assert.Single(events.OfType<AgentStreamEvent.ToolFinished>());
        Assert.True(result.IsError);
        Assert.Contains("不在工作区", result.Result);
        Assert.False(File.Exists(outside));

        Directory.Delete(workspace, recursive: true);
    }

    [Fact]
    public async Task PlainTaskWritesIntoHiddenFolderInUserProfile()
    {
        // 没选工作区：文件不该散在用户目录根上，要落进 ~/.avaloniaassistant
        var name = $"plain-{Guid.NewGuid():n}.md";
        var expected = Path.Combine(PlainWorkspace.Root, name);

        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.ToolUse(new ToolCallContent(
                "call-1",
                "write_file",
                new { path = name, content = "普通任务的产出" })),
            Faux.Response.Text("写好了。"),
        ]);

        var runtime = new PiAgentRuntime(new TestSettings(string.Empty), stream);
        var events = await CollectAsync(runtime, string.Empty);

        var result = Assert.Single(events.OfType<AgentStreamEvent.ToolFinished>());
        Assert.False(result.IsError, result.Result);
        Assert.Contains(PlainWorkspace.Root, result.Result);
        Assert.True(File.Exists(expected), result.Result);

        File.Delete(expected);
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(IAssistantRuntime runtime, string workspace)
    {
        var request = new AgentRunRequest(
            Guid.NewGuid().ToString("n"),
            "测试工具",
            [],
            "office",
            PermissionMode.Workspace,
            workspace,
            null);

        var events = new List<AgentStreamEvent>();
        await foreach (var @event in runtime.RunAsync(request))
        {
            events.Add(@event);
        }

        return events;
    }

    private sealed class ScriptedStreamFactory(IReadOnlyList<Faux.Response> responses) : IPiStreamFactory
    {
        public StreamFn Create(AssistantSettings settings) => Faux.CreateStreamFn(responses);
    }

    private sealed class TestSettings(string workspace) : ISettingsProvider
    {
        public AssistantSettings Current { get; } = new()
        {
            Provider = AgentProviderKind.DeepSeek,
            ApiKey = "test-key",
            BaseUrl = "http://localhost",
            Model = "faux-1",
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
