using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Agent;
using Pi.Agent;
using Pi.Ai.Providers;
using Pi.Ai.Types;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

public sealed class PiAgentRuntimeTests
{
    [Fact]
    public async Task RunsToolLoopAndStreamsFinalText()
    {
        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.ToolUse(new ToolCallContent("call-1", "current_time", new { })),
            Faux.Response.Text("现在是测试时间。"),
        ]);

        var runtime = new PiAgentRuntime(new FakeSettings(), stream);
        var events = await CollectAsync(runtime, "现在几点？");

        Assert.Contains(events, e => e is AgentStreamEvent.RunStarted);
        Assert.Contains(events, e => e is AgentStreamEvent.ToolStarted { Name: "current_time" });
        Assert.Contains(events, e => e is AgentStreamEvent.ToolFinished { IsError: false });
        Assert.Contains("现在是测试时间", string.Concat(events.OfType<AgentStreamEvent.TextDelta>().Select(e => e.Delta)));
        Assert.Contains(events, e => e is AgentStreamEvent.RunCompleted { StopReason: AgentStopReason.Completed });
    }

    [Fact]
    public async Task ReadOnlyBlocksMutatingToolBeforeExecution()
    {
        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.ToolUse(new ToolCallContent("call-1", "run_command", new { command = "echo hi" })),
            Faux.Response.Text("好的，我不执行。"),
        ]);

        var runtime = new PiAgentRuntime(new FakeSettings(), stream);
        var events = await CollectAsync(runtime, "帮我执行命令", PermissionMode.ReadOnly);

        var finished = Assert.Single(events.OfType<AgentStreamEvent.ToolFinished>());
        Assert.True(finished.IsError);
        Assert.Contains("只读", finished.Result);
    }

    [Fact]
    public async Task WorkspaceModeAsksGateForCommands()
    {
        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.ToolUse(new ToolCallContent("call-1", "run_command", new { command = "echo hi" })),
            Faux.Response.Text("已取消。"),
        ]);

        var gate = new RecordingGate(approve: false);
        var runtime = new PiAgentRuntime(new FakeSettings(), stream);
        var events = await CollectAsync(runtime, "执行 echo", PermissionMode.Workspace, gate);

        Assert.True(gate.Called);
        Assert.Equal("run_command", gate.ToolName);
        Assert.Contains(events, e => e is AgentStreamEvent.ToolFinished { IsError: true });
    }

    [Fact]
    public async Task MissingApiKeyFailsWithoutCallingModel()
    {
        var settings = new FakeSettings();
        settings.Current.ApiKey = string.Empty;
        var stream = new ScriptedStreamFactory([]);
        var runtime = new PiAgentRuntime(settings, stream);

        var events = await CollectAsync(runtime, "你好");

        Assert.Contains(events, e => e is AgentStreamEvent.RunFailed);
        Assert.False(stream.Created);
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(
        IAssistantRuntime runtime,
        string prompt,
        PermissionMode permission = PermissionMode.Workspace,
        IToolApprovalGate? gate = null)
    {
        var request = new AgentRunRequest(
            Guid.NewGuid().ToString("n"),
            prompt,
            [],
            "office",
            permission,
            Path.GetTempPath(),
            gate);

        var events = new List<AgentStreamEvent>();
        await foreach (var @event in runtime.RunAsync(request))
        {
            events.Add(@event);
        }

        return events;
    }

    private sealed class ScriptedStreamFactory : IPiStreamFactory
    {
        private readonly IReadOnlyList<Faux.Response> _responses;

        public ScriptedStreamFactory(IReadOnlyList<Faux.Response> responses)
        {
            _responses = responses;
        }

        public bool Created { get; private set; }

        public StreamFn Create(AssistantSettings settings)
        {
            Created = true;
            return Faux.CreateStreamFn(_responses);
        }
    }

    private sealed class FakeSettings : ISettingsProvider
    {
        private readonly AssistantSettings _settings = new()
        {
            Provider = AgentProviderKind.Custom,
            ApiKey = "test-key",
            BaseUrl = "http://localhost",
            Model = "faux-1",
            Permission = PermissionMode.Workspace,
            WorkspacePath = Path.GetTempPath(),
        };

        public AssistantSettings Current => _settings;

        public event EventHandler? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingGate(bool approve) : IToolApprovalGate
    {
        public bool Called { get; private set; }

        public string? ToolName { get; private set; }

        public Task<bool> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken)
        {
            Called = true;
            ToolName = request.ToolName;
            return Task.FromResult(approve);
        }
    }
}
