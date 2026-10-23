using System.Text;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent.Tools;

internal sealed class ListDirectoryTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "list_directory",
        "浏览文件夹",
        "列出文件夹里的文件和子目录（默认工作区根目录）。整理文件前先看一眼结构。",
        "文件",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo("path", "string", "文件夹路径，相对路径按工作区解析；默认 .", false),
        ]);

    public Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = JsonArgs.OptionalString(invocation.Json, "path") ?? ".";
        var full = ToolPaths.Resolve(invocation, path);
        if (!Directory.Exists(full))
        {
            throw new DirectoryNotFoundException($"文件夹不存在：{full}");
        }

        var builder = new StringBuilder();
        builder.AppendLine($"目录：{full}");

        var entries = new DirectoryInfo(full)
            .EnumerateFileSystemInfos()
            .OrderByDescending(info => info is DirectoryInfo)
            .ThenBy(info => info.Name, StringComparer.OrdinalIgnoreCase)
            .Take(300)
            .ToList();

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry is DirectoryInfo directory)
            {
                builder.AppendLine($"[目录] {directory.Name}/");
            }
            else if (entry is FileInfo file)
            {
                builder.AppendLine($"[文件] {file.Name}  {ToolText.HumanSize(file.Length)}  修改于 {file.LastWriteTime:yyyy-MM-dd HH:mm}");
            }
        }

        if (entries.Count == 0)
        {
            builder.AppendLine("（空目录）");
        }

        return Task.FromResult(builder.ToString());
    }
}

internal sealed class ReadFileTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "read_file",
        "读取文本文件",
        "读取工作区里的文本文件内容。默认最多返回 20000 个字符，超出会截断。",
        "文件",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo("path", "string", "文件路径，相对路径按工作区解析", true),
            new ToolParameterInfo("max_chars", "integer", "最多读取的字符数，默认 20000", false),
        ]);

    public async Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = JsonArgs.RequiredString(invocation.Json, "path");
        var maxChars = Math.Clamp(JsonArgs.OptionalInt(invocation.Json, "max_chars", 20_000), 500, 200_000);
        var full = ToolPaths.Resolve(invocation, path);

        if (!File.Exists(full))
        {
            throw new FileNotFoundException($"文件不存在：{full}");
        }

        var info = new FileInfo(full);
        if (info.Length > 8 * 1024 * 1024)
        {
            throw new InvalidOperationException($"文件太大（{ToolText.HumanSize(info.Length)}），请改用 run_command 做分段处理。");
        }

        await using var stream = File.OpenRead(full);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[maxChars + 1];
        var read = await reader.ReadBlockAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);

        if (read > 0 && buffer.AsSpan(0, read).Contains('\0'))
        {
            throw new InvalidOperationException("这看起来是二进制文件，无法按文本读取。");
        }

        var text = new string(buffer, 0, Math.Min(read, maxChars));
        var suffix = read > maxChars ? $"\n…（已截断，文件大小 {ToolText.HumanSize(info.Length)}）" : string.Empty;
        return $"文件：{full}\n\n{text}{suffix}";
    }
}

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

internal sealed class SearchFilesTool : IAgentTool
{
    private static readonly HashSet<string> SkippedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".vs", ".idea", "node_modules", "bin", "obj", "target", "dist", ".venv", "venv", "__pycache__",
    };

    public AgentToolInfo Info { get; } = new(
        "search_files",
        "搜索文件",
        "按文件名或文件内容在工作区里搜索，返回相对路径。找配置、找代码、找资料时使用。",
        "文件",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo("query", "string", "要搜索的关键字", true),
            new ToolParameterInfo("in_content", "boolean", "为 true 时同时搜索文件内容（仅文本文件），默认 false", false),
            new ToolParameterInfo("max_results", "integer", "最多返回条数，默认 40", false),
        ]);

    public async Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var query = JsonArgs.RequiredString(invocation.Json, "query");
        var inContent = JsonArgs.OptionalBool(invocation.Json, "in_content", false);
        var max = Math.Clamp(JsonArgs.OptionalInt(invocation.Json, "max_results", 40), 1, 100);
        var root = ToolPaths.Resolve(invocation, ".");

        var results = new List<string>();
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        foreach (var file in Directory.EnumerateFiles(root, "*", options))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (results.Count >= max)
            {
                break;
            }

            var relative = Path.GetRelativePath(root, file);
            if (relative.Split(Path.DirectorySeparatorChar).Any(part => SkippedDirectories.Contains(part)))
            {
                continue;
            }

            if (Path.GetFileName(file).Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(relative);
                continue;
            }

            if (!inContent)
            {
                continue;
            }

            try
            {
                var info = new FileInfo(file);
                if (info.Length > 1024 * 1024)
                {
                    continue;
                }

                var lines = await File.ReadAllLinesAsync(file, cancellationToken).ConfigureAwait(false);
                for (var index = 0; index < Math.Min(lines.Length, 4000); index++)
                {
                    if (lines[index].Contains(query, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add($"{relative}:{index + 1}: {lines[index].Trim()}");
                        break;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 跳过无法读取的文件。
            }
        }

        return results.Count == 0
            ? $"没有找到包含「{query}」的文件。"
            : $"找到 {results.Count} 个结果：\n" + string.Join("\n", results);
    }
}

internal sealed class OpenPathTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "open_path",
        "打开文件或文件夹",
        "用系统默认程序打开文件，或在资源管理器里打开文件夹。",
        "应用",
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

        Shell.Open(full);
        return Task.FromResult($"已打开：{full}");
    }
}
