using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent.Tools;

// 读文件、列目录、搜内容这三件事交给 Pi.CodingAgent 的 read / ls / grep / find：
// 它们遵循 .gitignore、支持按行区间读，模型训练时也见过同名工具。
// 这里只留 CodingAgent 没有、或默认权限下不启用的能力。

internal sealed class WriteFileTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "write_file",
        "写入文本文件",
        "把文本写入工作区里的文件（自动创建父目录）。只允许写工作区内部；只读模式下会被拦截。",
        "文件",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo("path", "string", "目标文件路径，相对路径按工作区解析", true),
            new ToolParameterInfo("content", "string", "要写入的完整文本", true),
            new ToolParameterInfo("append", "boolean", "为 true 时追加而不是覆盖，默认 false", false),
        ])
    {
        IsMutating = true,
    };

    public async Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = JsonArgs.RequiredString(invocation.Json, "path");
        var content = JsonArgs.OptionalString(invocation.Json, "content")
            ?? throw new InvalidOperationException("缺少参数 content。");
        var append = JsonArgs.OptionalBool(invocation.Json, "append", false);
        var full = ToolPaths.Resolve(invocation, path);

        var directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (append)
        {
            await File.AppendAllTextAsync(full, content, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(full, content, cancellationToken).ConfigureAwait(false);
        }

        return $"已{(append ? "追加" : "写入")} {content.Length} 个字符到 {full}。";
    }
}

internal sealed class OpenPathTool : IAgentTool
{
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".bat", ".cmd", ".com", ".msi", ".ps1", ".vbs", ".js", ".jar", ".lnk", ".scr",
    };

    public AgentToolInfo Info { get; } = new(
        "open_path",
        "打开文件或文件夹",
        "用系统默认程序打开文件，或在资源管理器里打开文件夹。",
        "文件",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo("path", "string", "要打开的文件或文件夹路径", true),
        ]);

    public Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = JsonArgs.RequiredString(invocation.Json, "path");
        var full = ToolPaths.Resolve(invocation, path);

        if (!File.Exists(full) && !Directory.Exists(full))
        {
            throw new FileNotFoundException($"路径不存在：{full}");
        }

        if (File.Exists(full) && ExecutableExtensions.Contains(Path.GetExtension(full)))
        {
            throw new InvalidOperationException(
                "不允许用 open_path 直接启动可执行文件。需要运行时请用 run_command，并让用户确认。");
        }

        Shell.Open(full);
        return Task.FromResult($"已打开：{full}");
    }
}
