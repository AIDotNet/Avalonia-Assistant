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

    /// <summary>
    /// 工具写完文件要推一条 FileChanged，且带真实 diff。
    /// 采集如果挂在界面侧就会坏在这里：事件走无界通道，界面收到「工具开始」时
    /// 一个几毫秒就能写完的工具早就结束了，快照拍到的是改之后的内容，diff 直接是空的。
    /// </summary>
    [Fact]
    public async Task ReportsFileChangeWithRealDiff()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "ava-run-artifact-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workspace);

        try
        {
            var stream = new ScriptedStreamFactory(
            [
                Faux.Response.ToolUse(new ToolCallContent(
                    "call-1",
                    "write_file",
                    new { path = "笔记.md", content = "第一行\n第二行" })),
                Faux.Response.Text("写好了。"),
            ]);

            var runtime = new PiAgentRuntime(new FakeSettings(), stream);
            var events = await CollectAsync(
                runtime,
                Guid.NewGuid().ToString("n"),
                "写个文件",
                PermissionMode.Workspace,
                workspace: workspace);

            var change = Assert.Single(events.OfType<AgentStreamEvent.FileChanged>());

            Assert.Equal(FileChangeKind.Created, change.Change.Kind);
            Assert.Equal(2, change.Change.AddedLines);
            Assert.Equal("笔记.md", change.Change.Path);
            Assert.Contains("+第一行", change.Change.Diff);

            // 断言文件真的落到工作区里，不然 diff 算得对也没意义
            Assert.True(File.Exists(Path.Combine(workspace, "笔记.md")));
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // 临时目录删不掉不影响断言
            }
        }
    }

    /// <summary>同一个文件被改第二次时，第二条记录要带【修改】而不是又一条【新建】。</summary>
    [Fact]
    public async Task SecondWriteIsModification()
    {
        var workspace = Path.Combine(Path.GetTempPath(), "ava-run-artifact2-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(workspace);
        await File.WriteAllTextAsync(Path.Combine(workspace, "a.txt"), "旧内容");

        try
        {
            var stream = new ScriptedStreamFactory(
            [
                Faux.Response.ToolUse(new ToolCallContent(
                    "call-1",
                    "write_file",
                    new { path = "a.txt", content = "新内容" })),
                Faux.Response.Text("改好了。"),
            ]);

            var runtime = new PiAgentRuntime(new FakeSettings(), stream);
            var events = await CollectAsync(
                runtime,
                Guid.NewGuid().ToString("n"),
                "改一下",
                PermissionMode.Workspace,
                workspace: workspace);

            var change = Assert.Single(events.OfType<AgentStreamEvent.FileChanged>());

            Assert.Equal(FileChangeKind.Modified, change.Change.Kind);
            Assert.Equal(1, change.Change.AddedLines);
            Assert.Equal(1, change.Change.RemovedLines);
            Assert.Contains("-旧内容", change.Change.Diff);
        }
        finally
        {
            try
            {
                Directory.Delete(workspace, recursive: true);
            }
            catch (IOException)
            {
                // 同上
            }
        }
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
    public async Task FullAccessRunsMutatingToolWithoutAskingGate()
    {
        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.ToolUse(new ToolCallContent("call-1", "run_command", new { command = "echo hi" })),
            Faux.Response.Text("执行完成。"),
        ]);

        // 完全访问下不应该再弹授权：gate 就算拒绝也未被动用。
        var gate = new RecordingGate(approve: false);
        var runtime = new PiAgentRuntime(new FakeSettings(), stream);
        var events = await CollectAsync(runtime, "执行 echo", PermissionMode.FullAccess, gate);

        Assert.False(gate.Called);
        Assert.Contains(events, e => e is AgentStreamEvent.ToolFinished { IsError: false });
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

    [Fact]
    public async Task SwitchingToReadOnlyRebuildsSessionAndBlocksMutatingTools()
    {
        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.ToolUse(new ToolCallContent("call-1", "run_command", new { command = "echo hi" })),
            Faux.Response.Text("好的，不执行。"),
        ]);

        var runtime = new PiAgentRuntime(new FakeSettings(), stream);
        var sessionId = Guid.NewGuid().ToString("n");

        // 第一轮工作区、无授权界面 → 被拦；第二轮切到只读 → 应在工具执行前被拦。
        await CollectAsync(runtime, sessionId, "第一句", PermissionMode.Workspace, history: []);
        var events = await CollectAsync(
            runtime,
            sessionId,
            "帮我执行命令",
            PermissionMode.ReadOnly,
            history: [HistoryTurn.User("第一句"), HistoryTurn.Assistant("好的，不执行。")]);

        var finished = Assert.Single(events.OfType<AgentStreamEvent.ToolFinished>());
        Assert.True(finished.IsError);
        Assert.Contains("只读", finished.Result);
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(
        IAssistantRuntime runtime,
        string prompt,
        PermissionMode permission = PermissionMode.Workspace,
        IToolApprovalGate? gate = null)
        => await CollectAsync(runtime, Guid.NewGuid().ToString("n"), prompt, permission, gate, []);

    /// <summary>
    /// 同一会话连续追问时不应重建 Agent 会话：重建会重扫技能、重启 MCP 子进程，
    /// 并把上一轮的工具结果丢在旧会话里。
    /// </summary>
    [Fact]
    public async Task ReusesSessionAcrossFollowUpTurns()
    {
        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.Text("第一轮回答。"),
            Faux.Response.Text("第二轮回答。"),
        ]);

        var runtime = new PiAgentRuntime(new FakeSettings(), stream);
        var sessionId = Guid.NewGuid().ToString("n");

        await CollectAsync(runtime, sessionId, "第一句", PermissionMode.Workspace, history: []);
        await CollectAsync(
            runtime,
            sessionId,
            "第二句",
            PermissionMode.Workspace,
            history: [HistoryTurn.User("第一句"), HistoryTurn.Assistant("第一轮回答。")]);

        Assert.Equal(1, stream.CreatedCount);
    }

    /// <summary>
    /// 只调工具、没有正文的那一轮不会进历史（BuildHistory 会跳过空文本），
    /// 这属于「接着长」，不能因此判定需要重建。
    /// </summary>
    [Fact]
    public async Task ReusesSessionWhenToolOnlyTurnAddsNoHistoryText()
    {
        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.Text("第一轮回答。"),
            Faux.Response.Text("第二轮回答。"),
            Faux.Response.Text("第三轮回答。"),
        ]);

        var runtime = new PiAgentRuntime(new FakeSettings(), stream);
        var sessionId = Guid.NewGuid().ToString("n");

        await CollectAsync(runtime, sessionId, "第一句", PermissionMode.Workspace, history: []);
        await CollectAsync(
            runtime,
            sessionId,
            "第二句",
            PermissionMode.Workspace,
            history: [HistoryTurn.User("第一句"), HistoryTurn.Assistant("第一轮回答。")]);
        await CollectAsync(
            runtime,
            sessionId,
            "第三句",
            PermissionMode.Workspace,
            history:
            [
                HistoryTurn.User("第一句"),
                HistoryTurn.Assistant("第一轮回答。"),
                HistoryTurn.User("第二句"),
            ]);

        Assert.Equal(1, stream.CreatedCount);
    }

    /// <summary>历史比上一轮短，说明后面的轮次被截掉了（重试），必须重建。</summary>
    [Fact]
    public async Task ShorterHistoryRebuildsSession()
    {
        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.Text("第一轮回答。"),
            Faux.Response.Text("第二轮回答。"),
            Faux.Response.Text("重试回答。"),
        ]);

        var runtime = new PiAgentRuntime(new FakeSettings(), stream);
        var sessionId = Guid.NewGuid().ToString("n");

        await CollectAsync(runtime, sessionId, "第一句", PermissionMode.Workspace, history: []);
        await CollectAsync(
            runtime,
            sessionId,
            "第二句",
            PermissionMode.Workspace,
            history: [HistoryTurn.User("第一句"), HistoryTurn.Assistant("第一轮回答。")]);
        await CollectAsync(runtime, sessionId, "重试第一句", PermissionMode.Workspace, history: []);

        Assert.Equal(2, stream.CreatedCount);
    }

    /// <summary>
    /// 采样参数是在装配时被 StreamSimple 的闭包捕获的。会话开始复用之后，
    /// 如果复用条件里不比这两项，改了 Temperature / MaxTokens 下一轮还是旧值。
    /// </summary>
    [Fact]
    public async Task ChangingSamplingSettingsRebuildsSession()
    {
        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.Text("第一轮回答。"),
            Faux.Response.Text("第二轮回答。"),
        ]);

        var settings = new FakeSettings();
        var runtime = new PiAgentRuntime(settings, stream);
        var sessionId = Guid.NewGuid().ToString("n");

        await CollectAsync(runtime, sessionId, "第一句", PermissionMode.Workspace, history: []);
        settings.Current.Temperature = 1.4;
        await CollectAsync(
            runtime,
            sessionId,
            "第二句",
            PermissionMode.Workspace,
            history: [HistoryTurn.User("第一句"), HistoryTurn.Assistant("第一轮回答。")]);

        Assert.Equal(2, stream.CreatedCount);
    }

    /// <summary>换了工作区必须重建：会话的沙箱和 cwd 都是装配时定下的。</summary>
    [Fact]
    public async Task SwitchingWorkspaceRebuildsSession()
    {
        var stream = new ScriptedStreamFactory(
        [
            Faux.Response.Text("第一轮回答。"),
            Faux.Response.Text("第二轮回答。"),
        ]);

        var runtime = new PiAgentRuntime(new FakeSettings(), stream);
        var sessionId = Guid.NewGuid().ToString("n");
        var first = Directory.CreateTempSubdirectory("aa-ws-a-").FullName;
        var second = Directory.CreateTempSubdirectory("aa-ws-b-").FullName;

        await CollectAsync(runtime, sessionId, "第一句", PermissionMode.Workspace, history: [], workspace: first);
        await CollectAsync(
            runtime,
            sessionId,
            "第二句",
            PermissionMode.Workspace,
            history: [HistoryTurn.User("第一句"), HistoryTurn.Assistant("第一轮回答。")],
            workspace: second);

        Assert.Equal(2, stream.CreatedCount);
    }

    private static async Task<List<AgentStreamEvent>> CollectAsync(
        IAssistantRuntime runtime,
        string sessionId,
        string prompt,
        PermissionMode permission,
        IToolApprovalGate? gate = null,
        IReadOnlyList<HistoryTurn>? history = null,
        string? workspace = null)
    {
        var request = new AgentRunRequest(
            sessionId,
            prompt,
            history ?? [],
            "office",
            permission,
            workspace ?? Path.GetTempPath(),
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

        /// <summary>装了几次会话：每装一次就会走到这里一次。</summary>
        public int CreatedCount { get; private set; }

        public StreamFn Create(AssistantSettings settings)
        {
            Created = true;
            CreatedCount++;
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

        public Task<ToolApprovalDecision> RequestAsync(
            ToolApprovalRequest request,
            CancellationToken cancellationToken)
        {
            Called = true;
            ToolName = request.ToolName;
            return Task.FromResult(approve ? ToolApprovalDecision.Allow : ToolApprovalDecision.Deny);
        }
    }
}
