namespace AvaloniaAssistant.Storage;

/// <summary>工作区里的一个可引用文件。相对路径用正斜杠，和 git、CLI 的写法一致。</summary>
public sealed record WorkspaceFileEntry(
    string RelativePath,
    string FullPath,
    string Name,
    long Size,
    DateTime ModifiedAt);

/// <summary>
/// 在输入框里打 @ 时用来找文件的那套索引：递归扫一遍工作区，按名字打分排序，
/// 跳过依赖目录和构建产物。参考 Path2AI 的 fs:search-files：
/// 文件名精确匹配 → 前缀 → 包含 → 路径包含 → 字符子序列，越像的排越前。
/// </summary>
public sealed class WorkspaceFileSearch
{
    /// <summary>一次最多记住这么多文件，再大的仓库也只给前 4000 个候选。</summary>
    private const int MaxFiles = 4000;

    /// <summary>缓存有效期。刚新建的文件要能马上搜到，所以不能缓存太久。</summary>
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(3);

    /// <summary>和 Path2AI 的 GREP_IGNORE_DIR_NAMES 对齐，另加几个本机常见的产物目录。</summary>
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", ".svn", ".hg", ".bzr",
        "dist", "build", "out", ".next", ".nuxt", ".output",
        "coverage", ".nyc_output", ".cache", ".parcel-cache",
        "vendor", "target", "bin", "obj", ".gradle",
        "__pycache__", ".pytest_cache", ".mypy_cache",
        ".venv", "venv", "env",
        // 本项目的产物目录，不该出现在候选里
        ".vs", ".idea", ".vscode", ".artifacts", ".library",
    };

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _gate = new();

    /// <summary>列一遍工作区（带缓存）。目录不存在返回空。</summary>
    public IReadOnlyList<WorkspaceFileEntry> List(string root)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var key = Path.GetFullPath(root);
        lock (_gate)
        {
            if (_cache.TryGetValue(key, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached.Files;
            }
        }

        var files = Scan(key);

        lock (_gate)
        {
            _cache[key] = new CacheEntry(files, DateTimeOffset.UtcNow + CacheTtl);

            // 缓存只服务当前工作区，换得多了就全清，不做精细淘汰
            if (_cache.Count > 8)
            {
                _cache.Clear();
            }
        }

        return files;
    }

    /// <summary>按查询词找文件。空查询按最近修改排，先把刚动过的摆出来。</summary>
    public IReadOnlyList<WorkspaceFileEntry> Search(string root, string? query, int limit = 20)
    {
        var files = List(root);
        if (files.Count == 0)
        {
            return [];
        }

        var value = (query ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return files
                .OrderByDescending(file => file.ModifiedAt)
                .Take(limit)
                .ToList();
        }

        var scored = new List<(WorkspaceFileEntry File, int Score)>();
        foreach (var file in files)
        {
            var score = Score(file.RelativePath, value);
            if (score < int.MaxValue)
            {
                scored.Add((file, score));
            }
        }

        return scored
            .OrderBy(item => item.Score)
            .ThenBy(item => item.File.RelativePath, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .Select(item => item.File)
            .ToList();
    }

    /// <summary>清缓存。导入/保存文件后想让新文件立刻能搜到就调它。</summary>
    public void Invalidate(string? root = null)
    {
        lock (_gate)
        {
            if (string.IsNullOrWhiteSpace(root))
            {
                _cache.Clear();
                return;
            }

            _cache.Remove(Path.GetFullPath(root));
        }
    }

    /// <summary>
    /// 打分，越小越靠前。和 Path2AI 的 scoreFileSearchMatch 同一套规则：
    /// 名字完全一致 0、前缀 1、名字包含 10+位置、路径包含 30+位置、子序列 100+间隙。
    /// </summary>
    public static int Score(string relativePath, string query)
    {
        var path = relativePath.Replace('\\', '/').ToLowerInvariant();
        var value = query.Replace('\\', '/').Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            return 0;
        }

        var name = path[(path.LastIndexOf('/') + 1)..];

        if (name == value)
        {
            return 0;
        }

        if (name.StartsWith(value, StringComparison.Ordinal))
        {
            return 1;
        }

        var nameIndex = name.IndexOf(value, StringComparison.Ordinal);
        if (nameIndex >= 0)
        {
            return 10 + nameIndex;
        }

        if (path == value)
        {
            return 20;
        }

        var pathIndex = path.IndexOf(value, StringComparison.Ordinal);
        if (pathIndex >= 0)
        {
            return 30 + pathIndex;
        }

        // 子序列：query 的字符必须按顺序出现，总分越低说明越紧凑
        var cursor = 0;
        var gap = 0;
        foreach (var character in value)
        {
            var next = path.IndexOf(character, cursor);
            if (next < 0)
            {
                return int.MaxValue;
            }

            gap += next - cursor;
            cursor = next + 1;
        }

        return 100 + gap;
    }

    private static List<WorkspaceFileEntry> Scan(string root)
    {
        var files = new List<WorkspaceFileEntry>();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));

        while (pending.Count > 0 && files.Count < MaxFiles)
        {
            var directory = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = directory.GetFileSystemInfos();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (files.Count >= MaxFiles)
                {
                    break;
                }

                if (entry is DirectoryInfo child)
                {
                    if (IgnoredDirectories.Contains(child.Name) || child.Name.StartsWith('.'))
                    {
                        continue;
                    }

                    pending.Push(child);
                    continue;
                }

                if (entry is not FileInfo file || file.Name.StartsWith('.'))
                {
                    continue;
                }

                var relative = Path.GetRelativePath(root, file.FullName).Replace('\\', '/');
                files.Add(new WorkspaceFileEntry(relative, file.FullName, file.Name, file.Length, file.LastWriteTime));
            }
        }

        return files;
    }

    private sealed record CacheEntry(IReadOnlyList<WorkspaceFileEntry> Files, DateTimeOffset ExpiresAt);
}
