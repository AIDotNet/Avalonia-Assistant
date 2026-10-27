using System.Text;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace AvaloniaAssistant.Agent.Tools;

/// <summary>
/// 把模型写的 Markdown 落成真正的办公文件。
/// <para>
/// 单位说明：Word 的字号是「半磅」，<c>FontSize</c> 写 22 就是 11pt；
/// 换算写在 <see cref="HalfPoints"/> 里，免得每个调用点各写一个魔法数字。
/// </para>
/// </summary>
internal static class DocumentWrite
{
    public static async Task<string> WriteAsync(string path, string content, CancellationToken cancellationToken)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return extension switch
        {
            ".docx" => await Task.Run(() => WriteWord(path, content), cancellationToken).ConfigureAwait(false),
            ".xlsx" => await Task.Run(() => WriteExcel(path, content), cancellationToken).ConfigureAwait(false),
            ".md" or ".markdown" or ".txt" => await WritePlainAsync(path, content, cancellationToken).ConfigureAwait(false),
            ".csv" => await WriteCsvAsync(path, content, cancellationToken).ConfigureAwait(false),
            ".pptx" => throw new InvalidOperationException(
                "暂时不能直接生成 pptx：没有母版和版式的文件，PowerPoint 打不开。需要幻灯片时先写 .docx 大纲，再用 Office 的「转为演示文稿」。"),
            _ => throw new InvalidOperationException(
                $"不支持生成 {extension} 文件。可写：.docx / .xlsx / .md / .txt / .csv。"),
        };
    }

    private static async Task<string> WritePlainAsync(string path, string content, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        return $"已写入 {content.Length} 个字符到 {path}。";
    }

    private static async Task<string> WriteCsvAsync(string path, string content, CancellationToken cancellationToken)
    {
        // 模型给的常常是 Markdown 表格，直接当 csv 存会带着竖线和分隔行
        var rows = ParseTableRows(content);
        if (rows.Count == 0)
        {
            await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
            return $"已写入 {content.Length} 个字符到 {path}。";
        }

        var builder = new StringBuilder();
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(",", row.Select(QuoteCsv)));
        }

        await File.WriteAllTextAsync(path, builder.ToString(), cancellationToken).ConfigureAwait(false);
        return $"已把 {rows.Count} 行写成 {path}。";
    }

    private static string QuoteCsv(string value)
        => value.Contains(',') || value.Contains('"')
            ? "\"" + value.Replace("\"", "\"\"") + "\""
            : value;

    private static int HalfPoints(double points) => (int)Math.Round(points * 2);

    private static string WriteWord(string path, string markdown)
    {
        var blocks = MarkdownBlocks.Parse(markdown);

        using var document = WordprocessingDocument.Create(path, WordprocessingDocumentType.Document);
        var mainPart = document.AddMainDocumentPart();
        mainPart.Document = new W.Document();
        var body = mainPart.Document.AppendChild(new W.Body());

        foreach (var block in blocks)
        {
            switch (block.Kind)
            {
                case MdBlockKind.Heading:
                    body.AppendChild(BuildHeading(block));
                    break;
                case MdBlockKind.Bullet:
                    body.AppendChild(BuildListParagraph(block, ordered: false));
                    break;
                case MdBlockKind.Numbered:
                    body.AppendChild(BuildListParagraph(block, ordered: true));
                    break;
                case MdBlockKind.Quote:
                    body.AppendChild(BuildQuote(block));
                    break;
                case MdBlockKind.Code:
                    body.AppendChild(BuildCode(block));
                    break;
                case MdBlockKind.Table:
                    body.AppendChild(BuildTable(block.Rows));
                    break;
                default:
                    body.AppendChild(BuildParagraph(block.Text));
                    break;
            }
        }

        // 空文档 Word 会报「内容有问题」，塞一个空段落兜底
        if (!body.HasChildren)
        {
            body.AppendChild(new W.Paragraph());
        }

        mainPart.Document.Save();
        return $"已生成 Word 文档 {path}（{blocks.Count} 个内容块）。";
    }

    private static W.Paragraph BuildHeading(MdBlock block)
    {
        var size = block.Level switch
        {
            1 => 20d,
            2 => 16d,
            3 => 13.5,
            _ => 12d,
        };

        var run = new W.Run(
            new W.RunProperties(
                new W.Bold(),
                new W.FontSize { Val = HalfPoints(size).ToString() }),
            new W.Text(block.Text) { Space = SpaceProcessingModeValues.Preserve });

        return new W.Paragraph(
            new W.ParagraphProperties(
                new W.SpacingBetweenLines
                {
                    Before = block.Level == 1 ? "240" : "180",
                    After = "100",
                }),
            run);
    }

    private static W.Paragraph BuildParagraph(string text)
    {
        var paragraph = new W.Paragraph(
            new W.ParagraphProperties(
                new W.SpacingBetweenLines { After = "100", Line = "300", LineRule = W.LineSpacingRuleValues.Auto }));

        foreach (var run in BuildRuns(text))
        {
            paragraph.AppendChild(run);
        }

        return paragraph;
    }

    private static W.Paragraph BuildListParagraph(MdBlock block, bool ordered)
    {
        var paragraph = new W.Paragraph(
            new W.ParagraphProperties(
                new W.SpacingBetweenLines { After = "60" },
                new W.Indentation { Left = ((block.Level + 1) * 360).ToString() }));

        // 手写项目符号而不是挂 numbering：没有 numbering.xml 时挂上去反而不显示
        var marker = ordered ? $"{block.Index}. " : "· ";
        paragraph.AppendChild(new W.Run(new W.Text(marker)));

        foreach (var run in BuildRuns(block.Text))
        {
            paragraph.AppendChild(run);
        }

        return paragraph;
    }

    private static W.Paragraph BuildQuote(MdBlock block)
    {
        var paragraph = new W.Paragraph(
            new W.ParagraphProperties(
                new W.ParagraphBorders(
                    new W.LeftBorder
                    {
                        Val = W.BorderValues.Single,
                        Size = 12,
                        Color = "D9D9D3",
                    }),
                new W.SpacingBetweenLines { After = "100" },
                new W.Indentation { Left = "360" }));

        foreach (var run in BuildRuns(block.Text))
        {
            paragraph.AppendChild(run);
        }

        return paragraph;
    }

    private static W.Paragraph BuildCode(MdBlock block)
    {
        var run = new W.Run(
            new W.RunProperties(
                new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" },
                new W.Color { Val = "3C3C38" },
                new W.FontSize { Val = HalfPoints(9.5).ToString() }),
            new W.Text(block.Text) { Space = SpaceProcessingModeValues.Preserve });

        return new W.Paragraph(
            new W.ParagraphProperties(
                new W.Shading { Val = W.ShadingPatternValues.Clear, Fill = "F6F6F3" },
                new W.SpacingBetweenLines { After = "100" }),
            run);
    }

    private static W.Table BuildTable(List<string[]> rows)
    {
        var table = new W.Table();

        // 子元素顺序是 schema 定死的：tblPr → tblGrid → tr。
        // 少了 tblGrid、或者 tcPr 排在段落后面，Word 都会弹「内容有问题」——
        // OpenXml 自己读回来不会报，所以只能靠 schema 校验发现。
        table.AppendChild(new W.TableProperties(
            new W.TableBorders(
                new W.TopBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D3" },
                new W.LeftBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D3" },
                new W.BottomBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D3" },
                new W.RightBorder { Val = W.BorderValues.Single, Size = 4, Color = "D9D9D3" },
                new W.InsideHorizontalBorder { Val = W.BorderValues.Single, Size = 4, Color = "E8E8E4" },
                new W.InsideVerticalBorder { Val = W.BorderValues.Single, Size = 4, Color = "E8E8E4" })));

        var columnCount = rows.Count == 0 ? 0 : rows.Max(row => row.Length);
        var grid = new W.TableGrid();
        for (var c = 0; c < columnCount; c++)
        {
            grid.AppendChild(new W.GridColumn());
        }

        table.AppendChild(grid);

        for (var i = 0; i < rows.Count; i++)
        {
            var row = new W.TableRow();

            foreach (var value in rows[i])
            {
                var cellParagraph = new W.Paragraph(
                    new W.ParagraphProperties(new W.SpacingBetweenLines { After = "20" }));

                if (i == 0)
                {
                    // 表头加粗 + 浅底，Word 里没有 thead 的概念，只能靠样式区分
                    cellParagraph.AppendChild(new W.Run(
                        new W.RunProperties(new W.Bold()),
                        new W.Text(value) { Space = SpaceProcessingModeValues.Preserve }));
                }
                else
                {
                    cellParagraph.AppendChild(new W.Run(
                        new W.Text(value) { Space = SpaceProcessingModeValues.Preserve }));
                }

                var properties = new W.TableCellProperties();
                if (i == 0)
                {
                    properties.AppendChild(new W.Shading
                    {
                        Val = W.ShadingPatternValues.Clear,
                        Fill = "F8F8F6",
                    });
                }

                // 单元格属性必须排在段落前面，构造函数按传入顺序落子元素
                row.AppendChild(new W.TableCell(properties, cellParagraph));
            }

            table.AppendChild(row);
        }

        return table;
    }

    /// <summary>把 **粗体** / `代码` / *斜体* 拆成多个 Run。</summary>
    private static IEnumerable<W.Run> BuildRuns(string text)
    {
        var runs = new List<W.Run>();

        foreach (var span in InlineSpan.Parse(text))
        {
            var properties = new W.RunProperties();

            if (span.Bold)
            {
                properties.AppendChild(new W.Bold());
            }

            if (span.Italic)
            {
                properties.AppendChild(new W.Italic());
            }

            if (span.Code)
            {
                properties.AppendChild(new W.RunFonts { Ascii = "Consolas", HighAnsi = "Consolas" });
                properties.AppendChild(new W.Color { Val = "A32D2D" });
            }

            runs.Add(new W.Run(properties, new W.Text(span.Text) { Space = SpaceProcessingModeValues.Preserve }));
        }

        if (runs.Count == 0)
        {
            runs.Add(new W.Run(new W.Text(string.Empty)));
        }

        return runs;
    }

    private static string WriteExcel(string path, string markdown)
    {
        var tables = ParseTables(markdown);
        if (tables.Count == 0)
        {
            // 没有表格就当一列文本，总比拒绝执行好
            tables.Add(new TableBlock("Sheet1", MarkdownBlocks.Parse(markdown)
                .Where(block => block.Kind is MdBlockKind.Paragraph or MdBlockKind.Heading)
                .Select(block => block.Text)
                .Where(text => text.Length > 0)
                .Select(text => new[] { text })
                .ToList()));
        }

        using var document = SpreadsheetDocument.Create(path, SpreadsheetDocumentType.Workbook);
        var workbookPart = document.AddWorkbookPart();
        workbookPart.Workbook = new S.Workbook();
        var sheets = workbookPart.Workbook.AppendChild(new S.Sheets());

        uint sheetId = 1;

        foreach (var table in tables)
        {
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            var sheetData = new S.SheetData();
            worksheetPart.Worksheet = new S.Worksheet(sheetData);

            // 列宽按内容估算，打开就是能读的样子，不用手动拉
            var columnCount = table.Rows.Count == 0 ? 1 : table.Rows.Max(row => row.Length);
            var columns = new S.Columns();
            for (var i = 1; i <= columnCount; i++)
            {
                var width = 12d;
                foreach (var row in table.Rows)
                {
                    if (i - 1 < row.Length)
                    {
                        // 中文按两个字符宽算，否则中文列会挤成一团
                        var measured = row[i - 1].Sum(ch => ch > 0x2E80 ? 2d : 1d);
                        width = Math.Max(width, Math.Min(48d, measured + 3));
                    }
                }

                columns.AppendChild(new S.Column
                {
                    Min = (uint)i,
                    Max = (uint)i,
                    Width = width,
                    CustomWidth = true,
                });
            }

            worksheetPart.Worksheet.InsertAt(columns, 0);

            for (var r = 0; r < table.Rows.Count; r++)
            {
                var row = new S.Row { RowIndex = (uint)(r + 1) };

                for (var c = 0; c < table.Rows[r].Length; c++)
                {
                    var cell = new S.Cell
                    {
                        CellReference = ColumnName(c) + (r + 1),
                        DataType = S.CellValues.InlineString,
                        InlineString = new S.InlineString(
                            new S.Text(table.Rows[r][c]) { Space = SpaceProcessingModeValues.Preserve }),
                    };

                    row.AppendChild(cell);
                }

                sheetData.AppendChild(row);
            }

            var name = SanitizeSheetName(table.Name, sheetId);
            sheets.AppendChild(new S.Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = sheetId,
                Name = name,
            });

            // 每个 part 各存各的：只存 workbook 的话，工作表内容不会落盘，
            // 打开是一片空白——文件能生成，但里面什么都没有。
            worksheetPart.Worksheet.Save();

            sheetId++;
        }

        workbookPart.Workbook.Save();
        return $"已生成表格 {path}（{tables.Count} 张工作表）。";
    }

    /// <summary>Excel 的工作表名有硬限制：31 个字符，且不能带 \ / ? * [ ]。</summary>
    private static string SanitizeSheetName(string name, uint fallbackIndex)
    {
        var cleaned = new string(name.Where(ch => ch is not ('\\' or '/' or '?' or '*' or '[' or ']' or ':')).ToArray()).Trim();

        if (cleaned.Length == 0)
        {
            return $"Sheet{fallbackIndex}";
        }

        return cleaned.Length <= 31 ? cleaned : cleaned[..31];
    }

    private static string ColumnName(int index)
    {
        var name = string.Empty;
        var value = index;

        do
        {
            name = (char)('A' + value % 26) + name;
            value = value / 26 - 1;
        }
        while (value >= 0);

        return name;
    }

    private sealed record TableBlock(string Name, List<string[]> Rows);

    /// <summary>拆出 Markdown 里的所有表格，表名取它前面最近的那个标题。</summary>
    private static List<TableBlock> ParseTables(string markdown)
    {
        var blocks = MarkdownBlocks.Parse(markdown);
        var tables = new List<TableBlock>();
        var currentName = "Sheet1";

        foreach (var block in blocks)
        {
            if (block.Kind == MdBlockKind.Heading)
            {
                currentName = block.Text;
                continue;
            }

            if (block.Kind == MdBlockKind.Table && block.Rows.Count > 0)
            {
                tables.Add(new TableBlock(currentName, block.Rows));
            }
        }

        return tables;
    }

    private static List<string[]> ParseTableRows(string content) => ParseTables(content)
        .SelectMany(table => table.Rows)
        .ToList();
}

internal enum MdBlockKind
{
    Paragraph,
    Heading,
    Bullet,
    Numbered,
    Quote,
    Code,
    Table,
}

internal sealed record MdBlock(
    MdBlockKind Kind,
    string Text,
    int Level = 0,
    int Index = 0,
    List<string[]>? RowData = null)
{
    public List<string[]> Rows => RowData ?? [];
}

/// <summary>
/// 够用即可的 Markdown 分块器。只认办公文档里真会出现的语法，
/// 不做完整实现——多支持一种语法，就多一处生成出错的概率。
/// </summary>
internal static class MarkdownBlocks
{
    private static readonly Regex HeadingPattern = new(@"^(#{1,6})\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex BulletPattern = new(@"^(\s*)[-*+]\s+(.*)$", RegexOptions.Compiled);
    private static readonly Regex NumberedPattern = new(@"^(\s*)(\d+)[.)]\s+(.*)$", RegexOptions.Compiled);

    public static List<MdBlock> Parse(string markdown)
    {
        var blocks = new List<MdBlock>();
        var lines = markdown.ReplaceLineEndings("\n").Split('\n');

        var paragraph = new StringBuilder();
        var code = new StringBuilder();
        var inCode = false;
        var tableRows = new List<string[]>();

        void FlushParagraph()
        {
            if (paragraph.Length == 0)
            {
                return;
            }

            blocks.Add(new MdBlock(MdBlockKind.Paragraph, paragraph.ToString().Trim()));
            paragraph.Clear();
        }

        void FlushTable()
        {
            if (tableRows.Count == 0)
            {
                return;
            }

            blocks.Add(new MdBlock(MdBlockKind.Table, string.Empty, RowData: tableRows));
            tableRows = [];
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();

            // 代码块内部原样保留，里面长得像标题的东西不是标题
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    blocks.Add(new MdBlock(MdBlockKind.Code, code.ToString().TrimEnd()));
                    code.Clear();
                    inCode = false;
                }
                else
                {
                    FlushParagraph();
                    FlushTable();
                    inCode = true;
                }

                continue;
            }

            if (inCode)
            {
                code.AppendLine(line);
                continue;
            }

            if (trimmed.Length == 0)
            {
                FlushParagraph();
                FlushTable();
                continue;
            }

            // 表格：以竖线开头且至少两列
            if (trimmed.StartsWith('|') && trimmed.Count(ch => ch == '|') >= 2)
            {
                FlushParagraph();

                // 分隔行 | --- | --- | 不是数据
                if (Regex.IsMatch(trimmed, @"^\|[\s:\-|]+\|$"))
                {
                    continue;
                }

                tableRows.Add(SplitRow(trimmed));
                continue;
            }

            FlushTable();

            var heading = HeadingPattern.Match(trimmed);
            if (heading.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Heading, heading.Groups[2].Value.Trim(), heading.Groups[1].Length));
                continue;
            }

            var bullet = BulletPattern.Match(line);
            if (bullet.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(
                    MdBlockKind.Bullet,
                    bullet.Groups[2].Value.Trim(),
                    bullet.Groups[1].Length / 2));
                continue;
            }

            var numbered = NumberedPattern.Match(line);
            if (numbered.Success)
            {
                FlushParagraph();
                blocks.Add(new MdBlock(
                    MdBlockKind.Numbered,
                    numbered.Groups[3].Value.Trim(),
                    numbered.Groups[1].Length / 2,
                    int.TryParse(numbered.Groups[2].Value, out var number) ? number : 1));
                continue;
            }

            if (trimmed.StartsWith('>'))
            {
                FlushParagraph();
                blocks.Add(new MdBlock(MdBlockKind.Quote, trimmed[1..].Trim()));
                continue;
            }

            if (paragraph.Length > 0)
            {
                paragraph.Append(' ');
            }

            paragraph.Append(trimmed);
        }

        FlushParagraph();
        FlushTable();

        if (code.Length > 0)
        {
            blocks.Add(new MdBlock(MdBlockKind.Code, code.ToString().TrimEnd()));
        }

        return blocks;
    }

    private static string[] SplitRow(string trimmed)
    {
        var inner = trimmed.Trim('|');
        return inner.Split('|')
            .Select(cell => cell.Trim().Replace("\\|", "|"))
            .ToArray();
    }
}

internal readonly record struct InlineSpan(string Text, bool Bold, bool Italic, bool Code)
{
    /// <summary>拆 **粗体**、*斜体*、`代码`。三者不嵌套——文档里真这么写的很少。</summary>
    public static List<InlineSpan> Parse(string text)
    {
        var spans = new List<InlineSpan>();
        var buffer = new StringBuilder();

        void Flush()
        {
            if (buffer.Length > 0)
            {
                spans.Add(new InlineSpan(buffer.ToString(), false, false, false));
                buffer.Clear();
            }
        }

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`' && TryFind(text, i + 1, '`', out var codeEnd))
            {
                Flush();
                spans.Add(new InlineSpan(text[(i + 1)..codeEnd], false, false, true));
                i = codeEnd;
                continue;
            }

            if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*'
                && TryFind(text, i + 2, '*', out var boldEnd))
            {
                Flush();
                spans.Add(new InlineSpan(text[(i + 2)..boldEnd].TrimEnd('*'), true, false, false));

                // boldEnd 是结尾第一个星号，还要跨过第二个，否则它会当成普通字符留在正文里
                i = text.Length > boldEnd + 1 && text[boldEnd + 1] == '*'
                    ? boldEnd + 1
                    : boldEnd;
                continue;
            }

            buffer.Append(text[i]);
        }

        Flush();
        return spans;
    }

    private static bool TryFind(string text, int start, char target, out int index)
    {
        for (var i = start; i < text.Length; i++)
        {
            if (text[i] == target)
            {
                index = i;
                return true;
            }
        }

        index = -1;
        return false;
    }
}
