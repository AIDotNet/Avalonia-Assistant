namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 一种协议在界面上的展示信息，以及它对应 pi.net 的 api id。
/// api id 是 pi 的协议分发键（<c>openai-completions</c> / <c>openai-responses</c> / <c>anthropic-messages</c>），
/// 写错不会回退，会直接报 “No API provider registered for api”。
/// </summary>
public sealed record ProtocolOption(
    AgentProtocol Kind,
    string Name,
    string Description,
    string ApiId);

public static class ProtocolCatalog
{
    public static readonly ProtocolOption Chat = new(
        AgentProtocol.OpenAiCompatible,
        "Chat Completions",
        "OpenAI /chat/completions，兼容面最广：vLLM、Ollama、One-API 和多数公司网关。",
        "openai-completions");

    public static readonly ProtocolOption Responses = new(
        AgentProtocol.OpenAiResponses,
        "Responses",
        "OpenAI /responses，支持 reasoning；只有官方端点或代理了 Responses 的网关可用。",
        "openai-responses");

    public static readonly ProtocolOption Messages = new(
        AgentProtocol.AnthropicMessages,
        "Anthropic Messages",
        "Claude /messages，消息结构、工具 schema、流式事件都和 OpenAI 不同。",
        "anthropic-messages");

    public static readonly IReadOnlyList<ProtocolOption> All = [Chat, Responses, Messages];

    public static ProtocolOption For(AgentProtocol kind)
        => All.FirstOrDefault(option => option.Kind == kind) ?? Chat;

    public static string ApiId(AgentProtocol kind) => For(kind).ApiId;
}
