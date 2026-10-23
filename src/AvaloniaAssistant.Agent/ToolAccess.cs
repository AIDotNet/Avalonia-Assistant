using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 工具的安全属性判定。自建工具读 <see cref="AgentToolInfo"/>，CodingAgent 的内置工具和
/// MCP 工具没有这份元数据，按名字分类。
/// </summary>
internal static class ToolAccess
{
    /// <summary>会改动磁盘 / 进程 / 系统的内置工具（Pi.CodingAgent 的 ToolIndex）。</summary>
    private static readonly HashSet<string> MutatingBuiltins = new(StringComparer.Ordinal)
    {
        "bash",
        "powershell",
        "edit",
        "write",
    };

    private const string McpPrefix = "mcp__";

    public static bool IsMutating(string name, AgentToolInfo? info)
    {
        if (info is not null)
        {
            return info.IsMutating;
        }

        return MutatingBuiltins.Contains(name) || name.StartsWith(McpPrefix, StringComparison.Ordinal);
    }

    public static bool RequiresApproval(string name, AgentToolInfo? info)
    {
        if (info is not null)
        {
            return info.RequiresApproval;
        }

        // 内置写工具与 MCP 工具都要逐次确认：MCP 服务端的实际行为无法在本地预判。
        return MutatingBuiltins.Contains(name) || name.StartsWith(McpPrefix, StringComparison.Ordinal);
    }
}
