using System.Text.Json;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 从工具调用里捞出「这个文件被改成了什么样」。
/// <para>
/// 采集放在界面层而不是工具层，是因为要盖住三类写入：本项目的 <c>write_file</c>、
/// CodingAgent 内置的 <c>edit</c> / <c>write</c>，以及将来接进来的同类工具。
/// 在工具外面看「调用前长什么样、调用后长什么样」，比让每个工具自己上报更不容易漏。
/// </para>
/// <para>
/// 抓不到的情况也说清楚：<c>run_command</c> 里跑脚本改的文件不在采集范围内，
/// 那时候连改了哪些文件都不知道，只能靠用户自己看命令输出。
/// </para>
/// </summary>
public sealed class ArtifactTracker(string? workspacePath)
{
    /// <summary>
    /// 解析相对路径用的根。不能只在构造时定一次：新任务在开口之前还能换工作区
    /// （输入框下面那个胶囊），换完之后这里还是旧目录，产物就会被记到错误的路径上。
    /// </summary>
    private string? _workspacePath = workspacePath;

    /// <summary>换了工作区之后跟着改。每轮开跑前调一次。</summary>
    public void Rebase(string? newWorkspacePath) => _workspacePath = newWorkspacePath;
    /// <summary>会改动文件的工具。不在表里的工具只记一次调用，不碰磁盘。</summary>
    private static readonly HashSet<string> WriteTools = new(StringComparer.OrdinalIgnoreCase)
    {
        "write_file",
        "write_document",
        "edit",
        "write",
        "create_file",
        "write_to_file",
    };

    /// <summary>参数里可能装路径的字段名。各家工具叫法不一样，挨个试。</summary>
    private static readonly string[] PathKeys =
    [
        "path", "file_path", "filePath", "filepath", "target", "filename",
    ];

    /// <summary>超过这个大小就不读进内存了：读一次几十兆，界面会卡住。</summary>
    private const long MaxSnapshotBytes = 2 * 1024 * 1024;

    /// <summary>只有这些扩展名才值得算 diff，二进制读出来全是乱码。</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".json", ".jsonc", ".cs", ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx",
        ".html", ".htm", ".css", ".scss", ".xml", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf",
        ".py", ".java", ".go", ".rs", ".rb", ".php", ".sql", ".sh", ".bash", ".ps1", ".bat", ".cmd",
        ".csv", ".tsv", ".log", ".props", ".targets", ".csproj", ".sln", ".slnx", ".gitignore", ".env",
    };

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.Ordinal);

    /// <summary>
    /// 工具可能并行执行，两个回调会同时碰这张表。读文件不放在锁里：
    /// 只在读写字典的那一下加锁，别让磁盘 IO 把另一个工具卡住。
    /// </summary>
    private readonly object _gate = new();

    private sealed record Pending(string Tool, string FullPath, string? Before, long BeforeSize, bool Existed);

    /// <summary>工具开始：先把文件现在的样子记下来，结束后才有得比。</summary>
    public void ObserveStarted(string callId, string toolName, string arguments)
    {
        if (!WriteTools.Contains(toolName) || string.IsNullOrEmpty(callId))
        {
            return;
        }

        var path = TryReadPath(arguments);
        if (path is null)
        {
            return;
        }

        var full = ToFullPath(path);

        try
        {
            if (!File.Exists(full))
            {
                Remember(callId, new Pending(toolName, full, null, 0, false));
                return;
            }

            var info = new FileInfo(full);
            if (info.Length > MaxSnapshotBytes || !IsText(full))
            {
                // 太大或二进制：只记「改过」，不记内容
                Remember(callId, new Pending(toolName, full, null, info.Length, true));
                return;
            }

            Remember(callId, new Pending(toolName, full, File.ReadAllText(full), info.Length, true));
        }
        catch (Exception)
        {
            // 读不到就当没观测到，不能因为采集失败影响工具本身
        }
    }

    /// <summary>
    /// 工具结束：对比磁盘现状，产出一条变更记录。
    /// 工具报错、参数里找不到路径、文件其实没变，都返回 null。
    /// </summary>
    public FileChangeRecord? ObserveFinished(string callId, string toolName, bool isError)
    {
        Pending? pending;
        lock (_gate)
        {
            if (!_pending.Remove(callId, out pending))
            {
                return null;
            }
        }

        if (isError)
        {
            // 工具失败时文件可能只写了一半，这时候给用户看 diff 反而是误导
            return null;
        }

        try
        {
            var exists = File.Exists(pending.FullPath);
            if (!exists && !pending.Existed)
            {
                return null;
            }

            if (!exists)
            {
                return new FileChangeRecord
                {
                    Path = ToDisplayPath(pending.FullPath),
                    FullPath = pending.FullPath,
                    Kind = FileChangeKind.Deleted,
                    RemovedLines = CountLines(pending.Before),
                    Size = 0,
                    Tool = toolName,
                };
            }

            var info = new FileInfo(pending.FullPath);
            var binary = info.Length > MaxSnapshotBytes || !IsText(pending.FullPath);
            var after = binary ? null : File.ReadAllText(pending.FullPath);

            var diff = TextDiff.Compute(pending.Before, after);

            // 判断「有没有变」不能看 diff：文件大到不做逐行比对时，diff 算出来是 0，但那不代表没改。
            // 文本能读就比文本；读不了（二进制 / 过大）就保守记一条，宁可多一条也不漏。
            var contentChanged = !pending.Existed
                || after is null
                || !string.Equals(pending.Before, after, StringComparison.Ordinal);

            // 内容一模一样：模型「写」了一次但没真改，不值得占一行
            if (!contentChanged)
            {
                return null;
            }

            return new FileChangeRecord
            {
                Path = ToDisplayPath(pending.FullPath),
                FullPath = pending.FullPath,
                Kind = pending.Existed ? FileChangeKind.Modified : FileChangeKind.Created,
                Diff = diff.Text.Length == 0 ? null : diff.Text,
                AddedLines = diff.Added,
                RemovedLines = diff.Removed,
                Size = info.Length,
                Tool = toolName,
            };
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>一轮结束时清掉没配上的调用，别让它跨轮泄漏。</summary>
    public void Reset()
    {
        lock (_gate)
        {
            _pending.Clear();
        }
    }

    private void Remember(string callId, Pending pending)
    {
        lock (_gate)
        {
            _pending[callId] = pending;
        }
    }

    private string ToFullPath(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return Path.GetFullPath(path);
        }

        var root = PlainWorkspace.Resolve(_workspacePath);
        return Path.GetFullPath(Path.Combine(root, path));
    }

    /// <summary>工作区内的文件显示相对路径，越界的退回完整路径——后者更短不了，但至少不骗人。</summary>
    private string ToDisplayPath(string full)
    {
        try
        {
            var root = Path.GetFullPath(PlainWorkspace.Resolve(_workspacePath))
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return full[(root.Length + 1)..].Replace('\\', '/');
            }
        }
        catch (Exception)
        {
            // 路径算不出来就显示完整的
        }

        return full;
    }

    private static bool IsText(string path)
    {
        var extension = Path.GetExtension(path);
        if (string.IsNullOrEmpty(extension))
        {
            // 没有扩展名的（Dockerfile、LICENSE）按文本处理，读坏了会被上层的异常兜住
            return true;
        }

        return TextExtensions.Contains(extension);
    }

    private static int CountLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var count = 1;
        foreach (var ch in text)
        {
            if (ch == '\n')
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// 参数是格式化过的 JSON 文本，正常情况下能解析；
    /// 被截断或压根不是 JSON（某些工具传裸字符串）时返回 null，宁可少一条产物也不误报。
    /// </summary>
    private static string? TryReadPath(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(arguments);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var key in PathKeys)
            {
                if (!document.RootElement.TryGetProperty(key, out var value))
                {
                    continue;
                }

                if (value.ValueKind == JsonValueKind.String)
                {
                    var path = value.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        return path;
                    }
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }
}
