namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// Agent 运行时。实现可以是 pi.net，也可以是别家；UI 只依赖这个接口。
/// </summary>
public interface IAssistantRuntime
{
    IReadOnlyList<ModelOption> Models { get; }

    IReadOnlyList<AgentToolInfo> Tools { get; }

    IAsyncEnumerable<AgentStreamEvent> RunAsync(
        AgentRunRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>中止指定会话正在进行的运行。没有运行时为空操作。</summary>
    void Abort(string sessionId);
}

/// <summary>逐次工具授权的回调。UI 在对话里弹出允许/拒绝卡片并等待用户选择。</summary>
public interface IToolApprovalGate
{
    Task<bool> RequestAsync(ToolApprovalRequest request, CancellationToken cancellationToken);
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
