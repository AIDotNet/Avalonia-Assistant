using System.Text.Json;
using AvaloniaAssistant.Abstractions;
using Pi.CodingAgent;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 扫描 CodingAgent 约定的目录：<c>~/.pi/agent/skills</c>、<c>&lt;工作区&gt;/.pi/skills</c>、
/// 两份 <c>mcp.json</c>。只读配置，不发起 MCP 连接——连不连得上由会话启动时决定。
/// </summary>
internal static class CapabilityScanner
{
    public static AgentCapabilities Scan(AssistantSettings settings)
    {
        var workspace = ResolveWorkspace(settings.WorkspacePath);
        var agentDir = Config.GetAgentDir();
        var projectDir = Path.Combine(workspace, Config.ConfigDirName);

        var skills = new List<AgentSkillInfo>();
        skills.AddRange(ReadSkills(Path.Combine(agentDir, "skills"), isProjectScope: false));
        skills.AddRange(ReadSkills(Path.Combine(projectDir, "skills"), isProjectScope: true));

        var servers = new List<McpServerInfo>();
        servers.AddRange(ReadMcpServers(Path.Combine(agentDir, "mcp.json"), isProjectScope: false));
        servers.AddRange(ReadMcpServers(Path.Combine(projectDir, "mcp.json"), isProjectScope: true));

        return new AgentCapabilities(
            BuiltinTools(settings.Permission),
            skills,
            servers,
            Path.Combine(projectDir, "skills"),
            Path.Combine(projectDir, "mcp.json"));
    }

    private static IReadOnlyList<AgentToolInfo> BuiltinTools(PermissionMode permission)
    {
        var tools = new List<AgentToolInfo>
        {
            Tool("read", "读取文件", "按行读取文本或图片，技能正文也靠它加载", "内置", false),
            Tool("grep", "全文搜索", "按正则搜索文件内容，遵循 .gitignore", "内置", false),
            Tool("find", "文件查找", "按 glob 查找文件，遵循 .gitignore", "内置", false),
            Tool("ls", "列目录", "列出目录内容", "内置", false),
        };

        if (permission != PermissionMode.ReadOnly)
        {
            tools.Add(Tool("edit", "精确编辑", "对文件做最小替换，需要确认", "内置", true));
        }

        return tools;
    }

    private static AgentToolInfo Tool(string name, string label, string description, string category, bool mutating)
        => new(name, label, description, category, RequiresApproval: mutating, Parameters: [])
        {
            IsMutating = mutating,
        };

    private static IEnumerable<AgentSkillInfo> ReadSkills(string root, bool isProjectScope)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<AgentSkillInfo>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var file = Path.Combine(dir, "SKILL.md");
            if (!File.Exists(file))
            {
                continue;
            }

            var (name, description) = ReadFrontmatter(file, Path.GetFileName(dir));
            results.Add(new AgentSkillInfo(name, description, dir, isProjectScope));
        }

        return results;
    }

    private static (string Name, string Description) ReadFrontmatter(string file, string fallbackName)
    {
        var name = fallbackName;
        var description = string.Empty;

        try
        {
            var inHeader = false;
            var seenHeader = false;
            foreach (var line in File.ReadLines(file))
            {
                var trimmed = line.Trim();
                if (trimmed == "---")
                {
                    if (!seenHeader)
                    {
                        inHeader = true;
                        seenHeader = true;
                        continue;
                    }

                    break;
                }

                if (!inHeader)
                {
                    if (trimmed.Length > 0)
                    {
                        break;
                    }

                    continue;
                }

                if (trimmed.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    name = trimmed[5..].Trim().Trim('"', '\'');
                }
                else if (trimmed.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    description = trimmed[12..].Trim().Trim('"', '\'');
                }
            }
        }
        catch (IOException)
        {
            // 读不到就只报目录名。
        }

        return (name, description);
    }

    private static IEnumerable<McpServerInfo> ReadMcpServers(string file, bool isProjectScope)
    {
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            if (!document.RootElement.TryGetProperty("mcpServers", out var servers)
                || servers.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            var results = new List<McpServerInfo>();
            foreach (var server in servers.EnumerateObject())
            {
                results.Add(new McpServerInfo(server.Name, DescribeTarget(server.Value), isProjectScope));
            }

            return results;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return [];
        }
    }

    private static string DescribeTarget(JsonElement entry)
    {
        if (entry.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
        {
            return url.GetString() ?? "http";
        }

        if (entry.TryGetProperty("command", out var command) && command.ValueKind == JsonValueKind.String)
        {
            var parts = new List<string> { command.GetString() ?? "stdio" };
            if (entry.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
            {
                foreach (var arg in args.EnumerateArray())
                {
                    if (arg.ValueKind == JsonValueKind.String && arg.GetString() is { Length: > 0 } text)
                    {
                        parts.Add(text);
                    }
                }
            }

            return string.Join(' ', parts);
        }

        return "stdio";
    }

    private static string ResolveWorkspace(string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            return Path.GetFullPath(workspacePath);
        }

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(fallback) ? fallback : Environment.CurrentDirectory;
    }
}
