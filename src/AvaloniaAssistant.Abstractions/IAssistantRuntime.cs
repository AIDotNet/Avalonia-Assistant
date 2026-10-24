namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// Agent 运行时。实现可以是 pi.net，也可以是别家；UI 只依赖这个接口。
/// </summary>
public interface IAssistantRuntime
{
    IReadOnlyList<ModelOption> Models { get; }

    IReadOnlyList<AgentToolInfo> Tools { get; }

    /// <summary>内置工具、技能与 MCP 配置的快照，供界面展示。</summary>
    AgentCapabilities Capabilities { get; }

    IAsyncEnumerable<AgentStreamEvent> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>中止指定会话正在进行的运行。没有运行时为空操作。</summary>
    void Abort(string sessionId);

    /// <summary>
    /// 用一份（可能还没保存的）设置发一次最小请求，验证 Key、地址、模型、协议能不能通。
    /// 设置页的「测试连接」走这里，不依赖当前会话。
    /// </summary>
    Task<ConnectionTestResult> TestConnectionAsync(
        AssistantSettings settings,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 真去起一次进程并握手，回答「配置在，它到底连上没有」。
    /// MCP 页用它显示状态；探测失败不影响后续会话自己的连接。
    /// </summary>
    Task<McpProbeResult> ProbeMcpServerAsync(
        string serverName,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 丢弃缓存中的会话，下次运行重新装配。装了新技能 / 改了 MCP 配置后调用，
    /// 否则要等应用重启才生效。
    /// </summary>
    void InvalidateSessions();
}

/// <summary>逐次工具授权的回调。UI 在对话里弹出允许/拒绝卡片并等待用户选择。</summary>
public interface IToolApprovalGate
{
    Task<bool> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
}

/// <summary>麦克风录音 + 语音转写。实现放在基础设施层，UI 只负责按钮状态。</summary>
public interface IVoiceInput
{
    bool IsRecording { get; }

    Task StartAsync(CancellationToken cancellationToken = default);

    Task<string> StopAndTranscribeAsync(CancellationToken cancellationToken = default);

    /// <summary>放弃当前录音，不转写。</summary>
    void Cancel();
}

public interface ISessionStore
{
    Task<IReadOnlyList<ChatSession>> LoadAllAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ChatSession session, CancellationToken cancellationToken = default);

    Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default);
}

public interface ISettingsProvider
{
    AssistantSettings Current { get; }

    Task LoadAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default);

    /// <summary>
    /// 保存成功后触发。<b>不保证在 UI 线程上</b>：实现里全程 ConfigureAwait(false)，
    /// 事件往往从线程池上抛出来，订阅方要自己回 UI 线程再改绑定源。
    /// </summary>
    event EventHandler? Changed;
}
