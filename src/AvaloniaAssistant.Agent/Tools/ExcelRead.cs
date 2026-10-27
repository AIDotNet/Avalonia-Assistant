using System.Text;
using DocumentFormat.OpenXml.Packaging;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace AvaloniaAssistant.Agent.Tools;

/// <summary>读 xlsx：每张工作表转成一张 Markdown 表，表名当标题。</summary>
internal static class ExcelRead
{
    /// <summary>每张表最多读多少行。几百行的表全灌进上下文，对模型没有增量信息。</summary>
    private const int MaxSheetRows = 200;

    public static string Read(string path)
    {
        using var document = SpreadsheetDocument.Open(path, false);
        var workbookPart = document.WorkbookPart;
        if (workbookPart?.Workbook?.Sheets is null)
        {
            return string.Empty;
        }

        var sharedStrings = workbookPart.SharedStringTablePart?.SharedStringTable;
        var builder = new StringBuilder();

        foreach (var sheet in workbookPart.Workbook.Sheets.Elements<S.Sheet>())
        {
            if (sheet.Id?.Value is not { } relationshipId)
            {
                continue;
            }

            if (workbookPart.GetPartById(relationshipId) is not WorksheetPart worksheetPart)
            {
                continue;
            }

            builder.Append("## ").AppendLine(sheet.Name?.Value ?? "Sheet");
            builder.AppendLine();

            var rows = new List<List<string>>();
            var sheetData = worksheetPart.Worksheet?.Elements<S.SheetData>().FirstOrDefault();

            if (sheetData is not null)
            {
                foreach (var row in sheetData.Elements<S.Row>())
                {
                    if (rows.Count >= MaxSheetRows)
                    {
                        break;
                    }

                    var values = new List<string>();
                    foreach (var cell in row.Elements<S.Cell>())
                    {
                        values.Add(CellText(cell, sharedStrings));
                    }

                    // 整行都空就别占位置了
                    if (values.Any(value => value.Length > 0))
                    {
                        rows.Add(values);
                    }
                }
            }

            if (rows.Count == 0)
            {
                builder.AppendLine("（空表）");
                builder.AppendLine();
                continue;
            }

            var width = rows.Max(item => item.Count);
            for (var i = 0; i < rows.Count; i++)
            {
                while (rows[i].Count < width)
                {
                    rows[i].Add(string.Empty);
                }

                builder.Append("| ").Append(string.Join(" | ", rows[i].Select(Escape))).AppendLine(" |");

                if (i == 0)
                {
                    builder.Append('|').Append(string.Join("|", Enumerable.Repeat(" --- ", width))).AppendLine("|");
                }
            }

            if (rows.Count >= MaxSheetRows)
            {
                builder.AppendLine($"(只读了前 {MaxSheetRows} 行)");
            }

            builder.AppendLine();
        }

        return builder.ToString().TrimEnd();
    }

    private static string Escape(string value) => value.Replace("|", "\\|").ReplaceLineEndings(" ").Trim();

    private static string CellText(S.Cell cell, S.SharedStringTable? sharedStrings)
    {
        // 内联字符串的值在 <is><t> 里，没有 <v> 节点。
        // 先按 CellValue 取会让这类单元格全部读成空——文件里明明有字。
        if (cell.DataType?.Value == S.CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        var value = cell.CellValue?.InnerText ?? string.Empty;
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // 文本单元格存的是共享字符串表的下标，不是文本本身
        if (cell.DataType?.Value == S.CellValues.SharedString
            && int.TryParse(value, out var index)
            && sharedStrings is not null
            && index >= 0
            && index < sharedStrings.ChildElements.Count)
        {
            return sharedStrings.ChildElements[index]?.InnerText ?? string.Empty;
        }

        // 日期在 xlsx 里是序列号。这里不猜格式：猜错成 1904 那种日期比不猜更误导。
        return value;
    }
}
