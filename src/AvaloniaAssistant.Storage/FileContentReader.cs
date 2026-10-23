using System.Text;

namespace AvaloniaAssistant.Storage;

/// <summary>
/// 发送前读引用文件的内容。行数和字数都封顶——
/// 一次 @ 一个十万行的日志，上下文会当场被顶爆。
/// </summary>
public static class FileContentReader
{
    /// <summary>单文件最多读这么多行，和 Path2AI 的 SELECTED_FILE_READ_MAX_LINES 一致。</summary>
    public const int MaxLines = 1000;

    /// <summary>单文件最多读这么多字，超出的按行截断。</summary>
    public const int MaxChars = 8000;

    /// <summary>能被当作文本读的扩展名。二进制文件不读正文，只让模型知道有这么个附件。</summary>
    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".md", ".markdown", ".txt", ".rst", ".log", ".csv", ".tsv",
        ".json", ".yml", ".yaml", ".toml", ".ini", ".cfg", ".conf",
        ".cs", ".csproj", ".props", ".targets", ".sln", ".slnx", ".axaml", ".xaml",
        ".js", ".ts", ".tsx", ".jsx", ".py", ".ps1", ".sh", ".bat", ".cmd",
        ".html", ".htm", ".css", ".scss", ".xml", ".sql", ".java", ".go", ".rs",
        ".gitignore", ".gitattributes", ".editorconfig", ".env",
    };

    public static bool IsTextFile(string path)
        => TextExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 读文件正文。返回 null 表示不是文本文件（或没读到），
    /// <paramref name="truncated"/> 说明是不是被行数/字数卡住了。
    /// </summary>
    public static (string? Text, int LineCount, bool Truncated) Read(
        string path,
        int maxLines = MaxLines,
        int maxChars = MaxChars)
    {
        if (!File.Exists(path) || !IsTextFile(path))
        {
            return (null, 0, false);
        }

        var builder = new StringBuilder();
        var lines = 0;
        var truncated = false;

        try
        {
            // StreamReader 按 BOM 判编码；没有 BOM 一律当 UTF-8，中文 GBK 文件会出现乱码而不是崩
            using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            while (reader.ReadLine() is { } line)
            {
                if (lines >= maxLines || builder.Length + line.Length >= maxChars)
                {
                    truncated = true;
                    break;
                }

                builder.AppendLine(line);
                lines++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, 0, false);
        }

        if (lines == 0 && builder.Length == 0)
        {
            return (string.Empty, 0, false);
        }

        return (builder.ToString(), lines, truncated);
    }
}
