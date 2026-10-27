using System.IO.Compression;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Agent.Tools;
using AvaloniaAssistant.Storage.Documents;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Validation;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 文档读写：写出去的文件必须能读回来，且结构不丢。
/// 办公产物一旦在转换这一步走样，用户拿到的是一份看起来正常、实际缺内容的文件——比直接报错更糟。
/// </summary>
public sealed class DocumentToolsTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ava-docs-" + Guid.NewGuid().ToString("n"));

    public DocumentToolsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录删不掉不影响断言结果
        }
    }

    [Fact]
    public async Task WritesAndReadsBackWord()
    {
        var path = Path.Combine(_root, "周报.docx");
        const string markdown = """
                                # 本周进展

                                完成了 **三件** 事，其中一件带 `代码`。

                                ## 数据

                                | 项目 | 数量 |
                                | --- | --- |
                                | 需求 | 12 |
                                | 缺陷 | 3 |

                                - 第一条
                                - 第二条

                                > 下周继续。

                                ```
                                dotnet test
                                ```
                                """;

        await DocumentWrite.WriteAsync(path, markdown, CancellationToken.None);

        Assert.True(File.Exists(path));

        var text = WordRead.Read(path);

        Assert.Contains("本周进展", text);
        Assert.Contains("完成了 三件 事", text);
        Assert.Contains("| 需求 | 12 |", text);
        Assert.Contains("- 第一条", text);

        // 引用在 Word 里是「缩进 + 左边框」，读回来就是普通段落，没有 > 前缀
        Assert.Contains("下周继续。", text);
        Assert.Contains("dotnet test", text);
    }

    /// <summary>
    /// 生成的文件必须过 Office 2019 的 schema 校验。
    /// 这比「自己写、自己读回来」严格得多：OpenXml 读自己产出的东西很宽松，
    /// 但 Word / Excel 遇到元素顺序不对、必需节点缺失，会直接弹「内容有问题」。
    /// </summary>
    [Fact]
    public async Task GeneratedWordPassesOfficeSchema()
    {
        var path = Path.Combine(_root, "校验.docx");
        await DocumentWrite.WriteAsync(path, """
                                            # 标题

                                            正文一段，带 **粗体** 和 `代码`。

                                            - 列表一
                                            - 列表二

                                            | 列 | 值 |
                                            | --- | --- |
                                            | 甲 | 1 |

                                            ```
                                            var x = 1;
                                            ```
                                            """, CancellationToken.None);

        using var document = WordprocessingDocument.Open(path, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019)
            .Validate(document)
            .Select(error => $"{error.Path?.XPath} · {error.Description}")
            .ToList();

        Assert.True(
            errors.Count == 0,
            "docx 未通过 schema 校验：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public async Task GeneratedExcelPassesOfficeSchema()
    {
        var path = Path.Combine(_root, "校验.xlsx");
        await DocumentWrite.WriteAsync(
            path,
            "## 表一\n\n| 名称 | 数量 |\n| --- | --- |\n| 内存 | 32 |",
            CancellationToken.None);

        using var document = SpreadsheetDocument.Open(path, false);
        var errors = new OpenXmlValidator(FileFormatVersions.Office2019)
            .Validate(document)
            .Select(error => $"{error.Path?.XPath} · {error.Description}")
            .ToList();

        Assert.True(
            errors.Count == 0,
            "xlsx 未通过 schema 校验：" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    [Fact]
    public async Task WritesEachTableAsExcelSheet()    {
        var path = Path.Combine(_root, "清单.xlsx");
        const string markdown = """
                                ## 硬件

                                | 名称 | 数量 |
                                | --- | --- |
                                | 内存 | 32 |
                                | 磁盘 | 512 |

                                ## 软件

                                | 名称 | 版本 |
                                | --- | --- |
                                | .NET | 10 |
                                """;

        await DocumentWrite.WriteAsync(path, markdown, CancellationToken.None);

        var text = ExcelRead.Read(path);

        Assert.Contains("## 硬件", text);
        Assert.Contains("| 内存 | 32 |", text);
        Assert.Contains("## 软件", text);
        Assert.Contains("| .NET | 10 |", text);
    }

    [Fact]
    public async Task ExcelWithoutTableFallsBackToTextColumn()
    {
        var path = Path.Combine(_root, "纯文本.xlsx");

        await DocumentWrite.WriteAsync(path, "# 标题\n\n一段说明。", CancellationToken.None);

        var text = ExcelRead.Read(path);

        Assert.Contains("标题", text);
        Assert.Contains("一段说明。", text);
    }

    [Fact]
    public async Task RejectsLegacyBinaryFormats()
    {
        var path = Path.Combine(_root, "旧文档.doc");
        await File.WriteAllBytesAsync(path, [0xD0, 0xCF, 0x11, 0xE0]);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DocumentRead.ReadAsync(path, 1000, CancellationToken.None));

        Assert.Contains("另存为", error.Message);
    }

    [Fact]
    public async Task RejectsPowerPointOutputWithReason()
    {
        var path = Path.Combine(_root, "演示.pptx");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => DocumentWrite.WriteAsync(path, "## 第一页\n内容", CancellationToken.None));

        Assert.Contains("pptx", error.Message);
    }

    [Fact]
    public async Task TruncatesLongDocumentWithNotice()
    {
        var path = Path.Combine(_root, "长文.md");
        await File.WriteAllTextAsync(path, new string('字', 500));

        var text = await DocumentRead.ReadAsync(path, 100, CancellationToken.None);

        Assert.Contains("已截断", text);
        Assert.True(text.Length < 300);
    }

    [Fact]
    public async Task ReadsCsvAsText()
    {
        var path = Path.Combine(_root, "a.csv");
        await File.WriteAllTextAsync(path, "名称,数量\n内存,32");

        var text = await DocumentRead.ReadAsync(path, 1000, CancellationToken.None);

        Assert.Contains("内存,32", text);
    }

    [Fact]
    public async Task WritesCsvFromMarkdownTable()
    {
        var path = Path.Combine(_root, "a2.csv");
        await DocumentWrite.WriteAsync(path, "| 名称 | 数量 |\n| --- | --- |\n| 内存 | 32 |", CancellationToken.None);

        var text = await File.ReadAllTextAsync(path);

        Assert.Contains("名称,数量", text);
        Assert.Contains("内存,32", text);
    }

    [Fact]
    public void ParsesHeadingLevelsListsAndTables()
    {
        var blocks = MarkdownBlocks.Parse("""
                                          # 一级
                                          ### 三级
                                          - 项目
                                          1. 有序
                                          | a | b |
                                          | --- | --- |
                                          | 1 | 2 |
                                          普通段落
                                          """);

        Assert.Equal(MdBlockKind.Heading, blocks[0].Kind);
        Assert.Equal(1, blocks[0].Level);
        Assert.Equal(3, blocks[1].Level);
        Assert.Equal(MdBlockKind.Bullet, blocks[2].Kind);
        Assert.Equal(MdBlockKind.Numbered, blocks[3].Kind);
        Assert.Equal(MdBlockKind.Table, blocks[4].Kind);

        // 分隔行 | --- | 不能当成数据行
        Assert.Equal(2, blocks[4].Rows.Count);
        Assert.Equal(MdBlockKind.Paragraph, blocks[5].Kind);
    }

    [Fact]
    public void KeepsCodeBlockContentVerbatim()
    {
        var blocks = MarkdownBlocks.Parse("""
                                          ```csharp
                                          # 这不是标题
                                          var x = 1;
                                          ```
                                          """);

        Assert.Single(blocks);
        Assert.Equal(MdBlockKind.Code, blocks[0].Kind);
        Assert.Contains("# 这不是标题", blocks[0].Text);
    }

    [Fact]
    public void SplitsInlineBoldCodeAndPlain()
    {
        var spans = InlineSpan.Parse("这是 **粗体** 和 `代码` 结束");

        Assert.Contains(spans, span => span.Bold && span.Text == "粗体");
        Assert.Contains(spans, span => span.Code && span.Text == "代码");
        Assert.Contains(spans, span => !span.Bold && !span.Code && span.Text.Contains("这是"));
    }
}

/// <summary>行级 diff：产物面板靠它说清「改了什么」，算错等于骗人。</summary>
public sealed class TextDiffTests
{
    [Fact]
    public void ReportsAddedAndRemovedLines()
    {
        var result = TextDiff.Compute("a\nb\nc", "a\nB\nc");

        Assert.Equal(1, result.Added);
        Assert.Equal(1, result.Removed);
        Assert.Contains("-b", result.Text);
        Assert.Contains("+B", result.Text);
    }

    [Fact]
    public void TreatsMissingBeforeAsAllAdded()
    {
        var result = TextDiff.Compute(null, "x\ny");

        Assert.Equal(2, result.Added);
        Assert.Equal(0, result.Removed);
    }

    [Fact]
    public void ReturnsEmptyWhenIdentical()
    {
        var result = TextDiff.Compute("same\ntext", "same\ntext");

        Assert.False(result.HasChanges);
        Assert.Empty(result.Text);
    }

    [Fact]
    public void CollapsesUnchangedRegionsIntoHunkHeader()
    {
        var before = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"line {i}"));
        var after = before.Replace("line 20", "line 20 changed");

        var result = TextDiff.Compute(before, after);

        Assert.Contains("@@", result.Text);

        // 40 行里只改一行，上下文各 3 行，渲染出来不该是整篇
        Assert.True(result.Text.Split('\n').Length < 15);
    }

    [Fact]
    public void IgnoresTrailingNewlineDifference()
    {
        var result = TextDiff.Compute("a\nb\n", "a\nb");

        Assert.False(result.HasChanges);
    }

    [Fact]
    public void ClampsExtremelyLongLines()
    {
        // 压缩成一行的 JSON、base64 之类，整行存进会话会把存档撑到几十兆
        var before = "a";
        var after = new string('x', 5000);

        var result = TextDiff.Compute(before, after);

        var longest = result.Text.Split('\n').Max(line => line.Length);
        Assert.True(longest < 500, $"单行长度 {longest} 没有收敛");
        Assert.Contains("…", result.Text);
    }

    [Fact]
    public void HandlesLargeFileWithoutBlowingUp()
    {
        var before = string.Join('\n', Enumerable.Range(1, 3000));
        var after = string.Join('\n', Enumerable.Range(1, 3000).Select(i => i == 1500 ? "changed" : i.ToString()));

        var result = TextDiff.Compute(before, after);

        // 超出行数规模上限时不做逐行比对，但必须说清楚，不能让面板以为「没变化」
        Assert.Contains("未生成逐行对比", result.Text);
    }
}

/// <summary>计划文本解析：模型给什么形状都得能读出来，读错了进度条就是假的。</summary>
public sealed class PlanTextTests
{
    [Fact]
    public void ParsesThreeStatuses()
    {
        var steps = PlanText.Parse("""
                                   - [x] 查目录
                                   - [~] 归档
                                   - [ ] 写清单
                                   """);

        Assert.Equal(3, steps.Count);
        Assert.Equal(PlanStepStatus.Done, steps[0].Status);
        Assert.Equal("查目录", steps[0].Text);
        Assert.Equal(PlanStepStatus.InProgress, steps[1].Status);
        Assert.Equal(PlanStepStatus.Pending, steps[2].Status);
    }

    [Fact]
    public void ToleratesOtherListMarkers()
    {
        var steps = PlanText.Parse("* [x] 一\n• [ ] 二");

        Assert.Equal(2, steps.Count);
        Assert.Equal(PlanStepStatus.Done, steps[0].Status);
    }

    [Fact]
    public void TreatsPlainLinesAsPendingSteps()
    {
        var steps = PlanText.Parse("先看目录\n再看文件");

        Assert.Equal(2, steps.Count);
        Assert.All(steps, step => Assert.Equal(PlanStepStatus.Pending, step.Status));
    }

    [Fact]
    public void IgnoresEmptyLines()
    {
        var steps = PlanText.Parse("- [ ] 一\n\n\n- [ ] 二");

        Assert.Equal(2, steps.Count);
    }

    [Fact]
    public void FormatsBackToTheSameShape()
    {
        var steps = new List<PlanStep>
        {
            new() { Text = "一", Status = PlanStepStatus.Done },
            new() { Text = "二", Status = PlanStepStatus.InProgress },
            new() { Text = "三", Status = PlanStepStatus.Pending },
        };

        var text = PlanText.Format(steps);
        var roundTrip = PlanText.Parse(text);

        Assert.Equal(3, roundTrip.Count);
        Assert.Equal(PlanStepStatus.InProgress, roundTrip[1].Status);
    }

    [Fact]
    public void CapsStepCount()
    {
        var many = string.Join('\n', Enumerable.Range(1, 40).Select(i => $"- [ ] 第 {i} 步"));

        var steps = PlanText.Parse(many);

        Assert.Equal(20, steps.Count);
    }
}
