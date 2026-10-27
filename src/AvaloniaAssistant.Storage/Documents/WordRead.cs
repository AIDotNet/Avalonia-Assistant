using System.Text;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace AvaloniaAssistant.Storage.Documents;

/// <summary>读 docx：段落和表格按文档顺序走，标题按样式还原成 Markdown 的 #。</summary>
public static class WordRead
{
    public static string Read(string path)
    {
        using var document = WordprocessingDocument.Open(path, false);
        var body = document.MainDocumentPart?.Document?.Body;
        if (body is null)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();

        // 段落和表格常常交替出现，分两趟遍历会把表格全甩到末尾
        foreach (var element in body.ChildElements)
        {
            switch (element)
            {
                case Paragraph paragraph:
                {
                    var text = ParagraphText(paragraph);
                    if (text.Length > 0)
                    {
                        builder.AppendLine(text);
                        builder.AppendLine();
                    }

                    break;
                }

                case Table table:
                    builder.AppendLine(TableToMarkdown(table));
                    builder.AppendLine();
                    break;
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string ParagraphText(Paragraph paragraph)
    {
        var builder = new StringBuilder();

        foreach (var run in paragraph.Descendants<Run>())
        {
            foreach (var child in run.ChildElements)
            {
                switch (child)
                {
                    case Text text:
                        builder.Append(text.Text);
                        break;
                    case TabChar:
                        builder.Append('\t');
                        break;
                    case Break:
                        builder.Append("\n");
                        break;
                }
            }
        }

        var value = builder.ToString().TrimEnd();

        // Word 靠样式区分标题，读出来要还原层级，否则模型看不出结构
        var style = paragraph.ParagraphProperties?.ParagraphStyleId?.Val?.Value;
        if (!string.IsNullOrEmpty(style) && style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(style["Heading".Length..], out var level) && level is >= 1 and <= 6)
        {
            return new string('#', level) + " " + value;
        }

        if (style is "Title")
        {
            return "# " + value;
        }

        if (paragraph.ParagraphProperties?.NumberingProperties is not null && value.Length > 0)
        {
            return "- " + value;
        }

        // 我们写 docx 时用「· 」当项目符号（不依赖 numbering.xml，Word 里也一定显示），
        // 读回来还原成 Markdown 的短横线，否则来回一趟格式就变了。
        if (value.Length > 2 && (value.StartsWith("· ", StringComparison.Ordinal) || value.StartsWith("• ", StringComparison.Ordinal)))
        {
            return "- " + value[2..];
        }

        return value;
    }

    private static string TableToMarkdown(Table table)
    {
        var rows = new List<List<string>>();

        foreach (var row in table.Elements<TableRow>())
        {
            var cells = new List<string>();

            foreach (var cell in row.Elements<TableCell>())
            {
                var text = string.Join(
                    " ",
                    cell.Elements<Paragraph>().Select(ParagraphText).Where(item => item.Length > 0));

                // 竖线会把 Markdown 表格撑破，转义掉
                cells.Add(text.Replace("|", "\\|").ReplaceLineEndings(" ").Trim());
            }

            if (cells.Count > 0)
            {
                rows.Add(cells);
            }
        }

        if (rows.Count == 0)
        {
            return string.Empty;
        }

        var width = rows.Max(item => item.Count);
        var builder = new StringBuilder();

        for (var i = 0; i < rows.Count; i++)
        {
            var cells = rows[i];
            while (cells.Count < width)
            {
                cells.Add(string.Empty);
            }

            builder.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");

            // 第一行后面补分隔行，Markdown 表格才成立
            if (i == 0)
            {
                builder.Append('|').Append(string.Join("|", Enumerable.Repeat(" --- ", width))).AppendLine("|");
            }
        }

        return builder.ToString().TrimEnd();
    }
}
