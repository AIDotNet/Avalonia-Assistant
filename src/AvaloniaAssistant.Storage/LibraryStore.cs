using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaloniaAssistant.Storage;

/// <summary>
/// 资料库里的一个条目。原文件复制进 <c>.library/files</c>，能读成文本的内容抽出来存进索引——
/// 搜索和 @ 引用都直接吃索引，不用再碰磁盘。
/// </summary>
public sealed class LibraryDocument
{
    public string Id { get; set; } = string.Empty;

    /// <summary>用户在界面里看到的名字，也就是原文件名。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>.library/files</c> 下的实际文件名（带 id 前缀，避免重名互相覆盖）。</summary>
    public string FileName { get; set; } = string.Empty;

    public long Size { get; set; }

    /// <summary>类型徽章：MD / PDF / PNG / FILE。</summary>
    public string Kind { get; set; } = "FILE";

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>列表里那一行摘要：文本类取首个非空行，其它类型说明为什么搜不到内容。</summary>
    public string? Summary { get; set; }

    /// <summary>抽出来的正文，限 <see cref="LibraryStore.MaxIndexedChars"/> 字。二进制文件为 null。</summary>
    public string? Text { get; set; }

    [JsonIgnore]
    public bool HasText => !string.IsNullOrEmpty(Text);
}

/// <summary>一次导入的结果。跳过和失败分开报，界面才好说清「3 个进了库，1 个重名跳过」。</summary>
public sealed record LibraryImportResult(
    IReadOnlyList<LibraryDocument> Imported,
    IReadOnlyList<string> Skipped,
    IReadOnlyList<string> Errors)
{
    public static LibraryImportResult Empty { get; } = new([], [], []);
}

/// <summary>
/// 工作区资料库：一个工作区一个 <c>.library</c>，索引是同目录下的 <c>index.json</c>。
/// 纯文件操作，不碰 UI，也不依赖 Agent——资料属于工作区，换工作区就换一批资料。
/// </summary>
public sealed class LibraryStore
{
    public const string FolderName = ".library";

    /// <summary>单个文件最多索引这么多字，够模型引用，也不至于把索引撑到几十兆。</summary>
    public const int MaxIndexedChars = 20000;

    /// <summary>超过这个大小的文件只登记，不抽正文。</summary>
    private const long MaxTextFileBytes = 8 * 1024 * 1024;

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".log", ".csv", ".tsv",
        ".json", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf",
        ".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".axaml", ".xaml",
        ".js", ".ts", ".tsx", ".jsx", ".py", ".ps1", ".sh", ".bat", ".cmd",
        ".html", ".htm", ".css", ".scss", ".xml", ".sql", ".java", ".go", ".rs",
        ".gitignore", ".gitattributes", ".editorconfig", ".env",
    };

    private static readonly JsonSerializerOptions IndexOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string RootFor(string workspace) => Path.Combine(workspace, FolderName);

    public static string FilesFor(string workspace) => Path.Combine(RootFor(workspace), "files");

    public static string IndexFileFor(string workspace) => Path.Combine(RootFor(workspace), "index.json");

    /// <summary>索引损坏或不存在时返回空列表，不要把资料库整个卡死。</summary>
    public async Task<IReadOnlyList<LibraryDocument>> LoadAsync(string workspace, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            return [];
        }

        var file = IndexFileFor(workspace);
        if (!File.Exists(file))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(file);
            var index = await JsonSerializer.DeserializeAsync<LibraryIndex>(stream, IndexOptions, cancellationToken)
                .ConfigureAwait(false);
            return index?.Documents ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// 把文件复制进资料库。同名的自动改成「xxx (2).md」，不覆盖已有条目；
    /// 逐个文件处理，单个失败不影响其它文件。
    /// </summary>
    public async Task<LibraryImportResult> ImportAsync(
        string workspace,
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            throw new InvalidOperationException("请先选择一个工作区，资料会存到工作区的 .library 目录里。");
        }

        var documents = (await LoadAsync(workspace, cancellationToken).ConfigureAwait(false)).ToList();
        var imported = new List<LibraryDocument>();
        var skipped = new List<string>();
        var errors = new List<string>();

        Directory.CreateDirectory(FilesFor(workspace));

        foreach (var source in sourcePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (string.IsNullOrWhiteSpace(source) || !File.Exists(source))
                {
                    errors.Add($"{Path.GetFileName(source)}：文件不存在");
                    continue;
                }

                var info = new FileInfo(source);
                if (info.Length > MaxTextFileBytes * 8)
                {
                    errors.Add($"{info.Name}：文件太大（{FormatSize(info.Length)}），没有收录");
                    continue;
                }

                // 同名同大小视作同一份，避免反复拖同一个文件把库撑满
                if (documents.Any(item => string.Equals(item.Name, info.Name, StringComparison.OrdinalIgnoreCase)
                                          && item.Size == info.Length))
                {
                    skipped.Add(info.Name);
                    continue;
                }

                var id = Guid.NewGuid().ToString("n")[..12];
                var displayName = UniqueName(documents, info.Name);
                var storedName = id + "_" + SanitizeName(displayName);
                var destination = Path.Combine(FilesFor(workspace), storedName);

                File.Copy(source, destination, overwrite: false);

                var text = TryReadText(destination, info.Length);
                var document = new LibraryDocument
                {
                    Id = id,
                    Name = displayName,
                    FileName = storedName,
                    Size = info.Length,
                    Kind = KindFor(info.Extension),
                    AddedAt = DateTimeOffset.Now,
                    Text = text,
                    Summary = Summarize(text),
                };

                documents.Add(document);
                imported.Add(document);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                errors.Add($"{Path.GetFileName(source)}：{ex.Message}");
            }
        }

        if (imported.Count > 0)
        {
            await SaveAsync(workspace, documents, cancellationToken).ConfigureAwait(false);
        }

        return new LibraryImportResult(imported, skipped, errors);
    }

    /// <summary>删除条目：先摘索引再删文件，反过来的话文件没了索引还在，列表里会留一条点不开的空记录。</summary>
    public async Task<bool> RemoveAsync(string workspace, string id, CancellationToken cancellationToken = default)
    {
        var documents = (await LoadAsync(workspace, cancellationToken).ConfigureAwait(false)).ToList();
        var document = documents.FirstOrDefault(item => item.Id == id);
        if (document is null)
        {
            return false;
        }

        documents.Remove(document);
        await SaveAsync(workspace, documents, cancellationToken).ConfigureAwait(false);

        try
        {
            var path = Path.Combine(FilesFor(workspace), document.FileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 索引已经摘掉了，物理文件删不掉最多剩个孤儿文件，不影响使用
        }

        return true;
    }

    /// <summary>资料库搜索：名字、摘要、正文都算命中，正文命中的条目才真正体现「建过索引」。</summary>
    public static bool Matches(LibraryDocument document, string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        var value = query.Trim();
        return document.Name.Contains(value, StringComparison.OrdinalIgnoreCase)
               || (document.Summary?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false)
               || (document.Text?.Contains(value, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static async Task SaveAsync(
        string workspace,
        IReadOnlyList<LibraryDocument> documents,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(RootFor(workspace));

        var file = IndexFileFor(workspace);
        var temp = file + ".tmp";

        await using (var stream = File.Create(temp))
        {
            await JsonSerializer.SerializeAsync(
                    stream,
                    new LibraryIndex { Documents = [.. documents] },
                    IndexOptions,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        File.Move(temp, file, overwrite: true);
    }

    /// <summary>只能按 UTF-8 读。中文 GBK 文本会被判成二进制——宁可搜不到，也不要往模型嘴里塞乱码。</summary>
    private static string? TryReadText(string path, long length)
    {
        if (length > MaxTextFileBytes)
        {
            return null;
        }

        if (!TextExtensions.Contains(Path.GetExtension(path)))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                return string.Empty;
            }

            var text = new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);

            // BOM 去掉，不然首个标题会带一个看不见的字符
            if (text.Length > 0 && text[0] == '\uFEFF')
            {
                text = text[1..];
            }

            return text.Length > MaxIndexedChars ? text[..MaxIndexedChars] : text;
        }
        catch (Exception ex) when (ex is DecoderFallbackException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? Summarize(string? text)
    {
        if (text is null)
        {
            return "二进制内容不建索引，只能按文件名搜到";
        }

        foreach (var line in text.Split('\n', 120))
        {
            var trimmed = line.Trim().TrimStart('#', ' ', '\t', '-', '*', '/');
            if (trimmed.Length >= 2)
            {
                return trimmed.Length > 80 ? trimmed[..80] + "…" : trimmed;
            }
        }

        return "（空文件）";
    }

    private static string UniqueName(IReadOnlyList<LibraryDocument> documents, string name)
    {
        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);
        var candidate = name;
        var index = 2;

        while (documents.Any(item => string.Equals(item.Name, candidate, StringComparison.OrdinalIgnoreCase)))
        {
            candidate = $"{stem} ({index++}){extension}";
        }

        return candidate;
    }

    private static string SanitizeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        return new string(chars);
    }

    private static string KindFor(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "" => "FILE",
            _ => ext.Length <= 5 ? ext.ToUpperInvariant() : ext[..4].ToUpperInvariant(),
        };
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }

    private sealed class LibraryIndex
    {
        public int Version { get; set; } = 1;

        public List<LibraryDocument> Documents { get; set; } = [];
    }
}
