using System.Text;
using UglyToad.PdfPig;

namespace AvaloniaAssistant.Agent.Tools;

/// <summary>
/// 把办公文档读成模型能用的文本，按扩展名分发。
/// <para>
/// 定位是「日常办公」，而真实的工作产物就在 docx / xlsx / pptx / pdf 里。
/// 只会 read 纯文本的助理，等于要求用户先把文件另存一遍，这一步没人愿意做。
/// </para>
/// <para>
/// 输出刻意靠近 Markdown：表格还是表格，标题还是标题。模型读到的形状和它写出来的一致，
/// 来回改的时候才不会走样。
/// </para>
/// </summary>
internal static class DocumentRead
{
    /// <summary>一次最多吐多少字符。超了截断并说明，别把上下文一次撑爆。</summary>
    public const int DefaultMaxChars = 24_000;

    public static async Task<string> ReadAsync(string path, int maxChars, CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"文件不存在：{path}");
        }

        var extension = Path.GetExtension(path).ToLowerInvariant();

        var text = extension switch
        {
            ".docx" => await Task.Run(() => WordRead.Read(path), cancellationToken).ConfigureAwait(false),
            ".xlsx" => await Task.Run(() => ExcelRead.Read(path), cancellationToken).ConfigureAwait(false),
            ".pptx" => await Task.Run(() => PowerPointRead.Read(path), cancellationToken).ConfigureAwait(false),
            ".pdf" => await Task.Run(() => ReadPdf(path), cancellationToken).ConfigureAwait(false),
            ".md" or ".markdown" or ".txt" or ".csv" or ".tsv" or ".json" or ".xml" or ".html" or ".htm"
                or ".cs" or ".js" or ".ts" or ".py" or ".yaml" or ".yml"
                => await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false),
            _ => throw Unknown(extension),
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            return $"（{Path.GetFileName(path)} 里没有读到文字。可能是扫描件、纯图片，或者文件是空的。）";
        }

        return text.Length > maxChars
            ? text[..maxChars] + $"\n\n…（已截断，全文约 {text.Length} 字符。要看后面，把 max_chars 调大或分段读。）"
            : text;
    }

    /// <summary>
    /// 老格式（.doc / .xls / .ppt）是二进制 OLE，OpenXml 打不开。
    /// 用户手上这类文件不少，与其抛一个看不懂的异常，不如说清楚该怎么办。
    /// </summary>
    private static InvalidOperationException Unknown(string extension) => extension switch
    {
        ".doc" or ".xls" or ".ppt" => new InvalidOperationException(
            $"旧版 {extension} 是二进制格式，读不了。请先用 Office / WPS 另存为 {extension}x 再试。"),
        ".docm" or ".xlsm" or ".pptm" => new InvalidOperationException(
            $"带宏的 {extension} 暂不支持。另存为不带宏的版本再读。"),
        _ => new InvalidOperationException(
            $"不支持读取 {extension} 文件。目前支持 docx / xlsx / pptx / pdf / 纯文本。"),
    };

    private static string ReadPdf(string path)
    {
        using var document = PdfDocument.Open(path);
        var builder = new StringBuilder();

        foreach (var page in document.GetPages())
        {
            var text = page.Text;
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            builder.Append("## 第 ").Append(page.Number).AppendLine(" 页");
            builder.AppendLine(text.Trim());
            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
}
