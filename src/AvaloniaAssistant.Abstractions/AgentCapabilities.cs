namespace AvaloniaAssistant.Abstractions;

/// <summary>一个被发现的技能（<c>SKILL.md</c>）。</summary>
public sealed record AgentSkillInfo(
    string Name,
    string Description,
    string Directory,
    bool IsProjectScope);

/// <summary>一份 MCP 服务端配置。这里只报配置，不代表已经连上。</summary>
public sealed record McpServerInfo(
    string Name,
    string Target,
    bool IsProjectScope);

/// <summary>
/// 一次真实的 MCP 握手结果：起进程、initialize、tools/list。
/// 只回答「它到底连上没有、暴露了几个工具」，不代替会话里的连接。
/// </summary>
public sealed record McpProbeResult(bool Ok, int ToolCount, string Detail)
{
    public static McpProbeResult Failed(string detail) => new(false, 0, detail);
}

/// <summary>
/// 本次会话可用的能力清单。内置工具来自 Pi.CodingAgent 的工具目录，技能与 MCP 来自
/// <c>~/.pi/agent</c> 和工作区下的 <c>.pi</c>。
/// </summary>
public sealed record AgentCapabilities(
    IReadOnlyList<AgentToolInfo> BuiltinTools,
    IReadOnlyList<AgentSkillInfo> Skills,
    IReadOnlyList<McpServerInfo> McpServers,
    string SkillsRoot,
    string McpConfigPath)
{
    public static readonly AgentCapabilities Empty = new([], [], [], string.Empty, string.Empty);
}
