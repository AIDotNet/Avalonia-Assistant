namespace AvaloniaAssistant.Abstractions;

public enum AgentStopReason
{
    Completed,
    Aborted,
    Length,
    Error,
}

/// <summary>
/// 运行时向界面推送的流式事件。UI 只认识这一组类型，不认识 pi.net。
/// </summary>
public abstract record AgentStreamEvent
{
    private AgentStreamEvent()
    {
    }

    public sealed record RunStarted(string SessionId, string Model) : AgentStreamEvent;

    public sealed record TextDelta(string Delta) : AgentStreamEvent;

    public sealed record ThinkingDelta(string Delta) : AgentStreamEvent;

    public sealed record ToolStarted(string CallId, string Name, string Label, string Arguments) : AgentStreamEvent;

    public sealed record ToolFinished(
        string CallId,
        string Name,
        string? Result,
        bool IsError,
        long? DurationMs) : AgentStreamEvent;

    public sealed record RunCompleted(
        AgentStopReason StopReason,
        string? Error,
        long InputTokens,
        long OutputTokens,
        string? Model) : AgentStreamEvent;

    public sealed record RunFailed(string Message) : AgentStreamEvent;
}

public sealed record ModelOption(
    string Id,
    string Name,
    string ProviderName,
    string Description);

public sealed record ToolParameterInfo(
    string Name,
    string Type,
    string Description,
    bool Required);

public sealed record AgentToolInfo(
    string Name,
    string Label,
    string Description,
    string Category,
    bool RequiresApproval,
    IReadOnlyList<ToolParameterInfo> Parameters)
{
    /// <summary>会改变本机状态（写文件、执行命令、改系统设置）。只读模式下统一拦截。</summary>
    public bool IsMutating { get; init; }
}

public sealed record HistoryTurn(string Role, string Text)
{
    public static HistoryTurn User(string text) => new("user", text);

    public static HistoryTurn Assistant(string text) => new("assistant", text);
}

/// <summary>
/// 用户按下发送时交给对话线的东西：输入框里的原文，加上这一轮 @ 引用进来的文件。
/// 原文照原样显示在气泡里，附件内容只喂给模型。
/// </summary>
public sealed record PromptRequest(
    string Text,
    IReadOnlyList<ChatAttachment> Attachments,
    IReadOnlyList<ChatImage>? Images = null)
{
    public static PromptRequest Plain(string text) => new(text, []);

    public bool HasImages => Images is { Count: > 0 };
}

/// <summary>一次 agent 运行的输入。运行时无状态：历史由调用方携带。</summary>
public sealed record AgentRunRequest(    string SessionId,
    string Prompt,
    IReadOnlyList<HistoryTurn> History,
    string Mode,
    PermissionMode Permission,
    string? WorkspacePath,
    IToolApprovalGate? ApprovalGate,
    IReadOnlyList<ChatImage>? Images = null);

public sealed record ToolApprovalRequest(
    string SessionId,
    string ToolName,
    string Label,
    string Arguments,
    string Reason,
    PermissionMode Permission);
