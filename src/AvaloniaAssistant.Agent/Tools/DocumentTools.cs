using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage.Documents;

namespace AvaloniaAssistant.Agent.Tools;

/// <summary>
/// 读 Word / Excel / PPT / PDF。定位是日常办公，产物本来就在这些文件里，
/// 只会 read 纯文本的助理等于要求用户先另存一遍。
/// </summary>
internal sealed class ReadDocumentTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "read_document",
        "读取文档",
        "读取 docx / xlsx / pptx / pdf 里的文字和表格，输出接近 Markdown 的结构。"
        + "读 Word 会保留标题层级和表格；读 Excel 每张工作表转成一张表（默认每表最多 200 行）；"
        + "读 PPT 按页给文字并带上备注；读 PDF 按页给正文（扫描件没有文字层，读出来是空的）。",
        "文件",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo("path", "string", "文档路径，相对路径按工作区解析", true),
            new ToolParameterInfo(
                "max_chars",
                "integer",
                $"最多返回多少字符，默认 {DocumentRead.DefaultMaxChars}。内容长就分段读。",
                false),
        ]);

    public async Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = JsonArgs.RequiredString(invocation.Json, "path");
        var maxChars = JsonArgs.OptionalInt(invocation.Json, "max_chars", DocumentRead.DefaultMaxChars);

        if (maxChars <= 0)
        {
            maxChars = DocumentRead.DefaultMaxChars;
        }

        var full = ToolPaths.Resolve(invocation, path);
        return await DocumentRead.ReadAsync(full, maxChars, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// 把 Markdown 落成 docx / xlsx。办公场景的交付物是能发出去的文件，
/// 不是聊天窗口里的一段字——这一步不做，前面干得再好也交不了差。
/// </summary>
internal sealed class WriteDocumentTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "write_document",
        "生成文档",
        "把 Markdown 写成真正的办公文件，按扩展名决定格式："
        + ".docx 会还原标题层级、项目符号、表格和代码块；"
        + ".xlsx 把每个 Markdown 表格写成一张工作表（表名取表格前最近的标题）；"
        + ".md / .txt / .csv 直接写。写 .pptx 会失败——没有母版的幻灯片打不开，需要先出 docx 大纲。",
        "文件",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo("path", "string", "目标文件路径，扩展名决定格式（.docx / .xlsx / .md / .txt / .csv）", true),
            new ToolParameterInfo("content", "string", "文件内容。生成 docx / xlsx 时写 Markdown。", true),
        ])
    {
        IsMutating = true,
    };

    public async Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var path = JsonArgs.RequiredString(invocation.Json, "path");
        var content = JsonArgs.OptionalString(invocation.Json, "content")
            ?? throw new InvalidOperationException("缺少参数 content。");

        var full = ToolPaths.Resolve(invocation, path);
        return await DocumentWrite.WriteAsync(full, content, cancellationToken).ConfigureAwait(false);
    }
}
