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

    /// <summary>确定只读的内置工具。不在这些名字里、又没有元数据的工具一律按「会改本机」对待。</summary>
    private static readonly HashSet<string> ReadOnlyBuiltins = new(StringComparer.Ordinal)
    {
        "read",
        "grep",
        "find",
        "ls",
    };

    private const string McpPrefix = "mcp__";

    public static bool IsMutating(string name, AgentToolInfo? info)
    {
        if (info is not null)
        {
            return info.IsMutating;
        }

        if (MutatingBuiltins.Contains(name) || name.StartsWith(McpPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        // 插件带的工具（pi 包注册的）没有元数据，读文件、起进程都可能在内部发生；
        // 不能因为「名字不认识」就当只读放行。
        return !ReadOnlyBuiltins.Contains(name);
    }

    public static bool RequiresApproval(string name, AgentToolInfo? info)
    {
        if (info is not null)
        {
            return info.RequiresApproval;
        }

        // 内置写工具、MCP 工具、插件工具都要逐次确认：实际行为无法在本地预判。
        if (MutatingBuiltins.Contains(name) || name.StartsWith(McpPrefix, StringComparison.Ordinal))
        {
            return true;
        }

        return !ReadOnlyBuiltins.Contains(name);
    }
}
