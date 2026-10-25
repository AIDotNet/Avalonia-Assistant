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

    /// <summary>
    /// 用给定的设置探一次模型服务。设置页的「测试连接」用：
    /// 让「Key 填错 / Base URL 写错 / 模型名不存在」在保存后就暴露，而不是等发第一条消息。
    /// </summary>
    Task<ConnectionProbe> ProbeAsync(AssistantSettings settings, CancellationToken cancellationToken = default);

    /// <summary>中止指定会话正在进行的运行。没有运行时为空操作。</summary>
    void Abort(string sessionId);

    /// <summary>
    /// 丢弃这一个会话的缓存，下次运行重新装配。重试要重跑某一轮时用：
    /// 复用缓存会让模型在旧会话里看到一遍失败的回答，等于问了两次。
    /// </summary>
    void InvalidateSession(string sessionId);

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

    event EventHandler? Changed;
}
