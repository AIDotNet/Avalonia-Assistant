using System.Text;

namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 把用户原话和 @ 引用的文件拼成真正发给模型的文本。
/// 结构参考 Path2AI：用一段 <c>system-reminder</c> 说明「这些内容应用已经读过」，
/// 免得模型又去 grep、read 一遍同样的文件——截图里那串 run_command 有一半是这么来的。
/// </summary>
public static class PromptComposer
{
    /// <summary>一次提问最多携带这么多字的文件内容，超出的部分截断并注明。</summary>
    public const int DefaultMaxAttachmentChars = 24000;

    private const string ReminderOpen = "<system-reminder>";
    private const string ReminderClose = "</system-reminder>";

    public static string Compose(
        string text,
        IReadOnlyList<ChatAttachment>? attachments,
        int maxTotalChars = DefaultMaxAttachmentChars,
        string? mode = null)
    {
        var hasAttachments = attachments is { Count: > 0 };
        var hasMode = !string.IsNullOrWhiteSpace(mode);
        if (!hasAttachments && !hasMode)
        {
            return text;
        }

        var remaining = maxTotalChars;

        var builder = new StringBuilder();
        if (hasMode)
        {
            // 模式以前只是界面上的高亮，模型完全收不到。做成一条 system-reminder，
            // 和引用文件走同一种「应用已经替你准备好了」的表达方式。
            builder.AppendLine(ReminderOpen);
            builder.AppendLine($"当前工作模式：{WorkModeText.Describe(mode)}。{WorkModeText.Directive(mode)}");
            builder.AppendLine(ReminderClose);
            builder.AppendLine();
        }

        builder.AppendLine(text);

        if (!hasAttachments)
        {
            return builder.ToString();
        }

        builder.AppendLine();
        builder.AppendLine(ReminderOpen);
        builder.AppendLine("用户在这条消息里引用了下面的文件，应用已在发送前读过内容。");
        builder.AppendLine("请直接把它们当作已提供的上下文，不要再去搜索或重复读取同一路径。");
        builder.AppendLine("<selected_files>");

        foreach (var attachment in attachments!)
        {
            if (remaining <= 0)
            {
                builder.AppendLine("[其余引用因长度限制没有展开]");
                break;
            }

            builder.AppendLine($"## {attachment.DisplayPath}");

            if (!string.IsNullOrWhiteSpace(attachment.Error))
            {
                builder.AppendLine($"[读取失败：{Sanitize(attachment.Error!)}]");
                continue;
            }

            if (!attachment.HasText)
            {
                builder.AppendLine($"[{attachment.Kind} 文件，{attachment.Size} 字节，无法读取为文本]");
                continue;
            }

            var body = Trim(attachment.Text!, remaining);
            remaining -= body.Length;
            builder.AppendLine(Sanitize(body));

            if (attachment.Truncated)
            {
                builder.AppendLine(attachment.LineCount > 0
                    ? $"[只读了前 {attachment.LineCount} 行]"
                    : "[内容过长，已截断]");
            }
        }

        builder.AppendLine("</selected_files>");
        builder.AppendLine(ReminderClose);

        return builder.ToString();
    }

    /// <summary>按剩余预算截断，并且不把代理对（emoji）切成半个字符。</summary>
    private static string Trim(string text, int budget)
    {
        if (text.Length <= budget)
        {
            return text;
        }

        var cut = budget;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "\n（内容过长，已截断）";
    }

    /// <summary>正文里的标签要打散，否则模型会把文件内容误当成系统提示。</summary>
    private static string Sanitize(string text)
        => text
            .Replace(ReminderOpen, "<system-reminder-data>", StringComparison.OrdinalIgnoreCase)
            .Replace(ReminderClose, "<\\/system-reminder>", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 「办公 / 开发 / 设计」三个档位的文案与行为约束。
/// 之前只有界面上的高亮，运行时读不到，等于一个假开关；现在由 PromptComposer 注入模型。
/// </summary>
public static class WorkModeText
{
    public const string Office = "office";

    public const string Development = "development";

    public const string Design = "design";

    public static string Describe(string? mode) => mode switch
    {
        Development => "开发",
        Design => "设计",
        _ => "办公",
    };

    /// <summary>给模型的一句话行为约束。写得越具体，三个档位的差别越看得出来。</summary>
    public static string Directive(string? mode) => mode switch
    {
        Development => "先读现有代码再动手；改动尽量小、贴合项目已有写法；改完自己跑一遍构建或测试再回话。",
        Design => "先对齐信息层级和视觉规范再给方案；给到具体的间距、字号、颜色，不要泛泛而谈。",
        _ => "以文档、表格、汇报材料为主要产出；先给结论再给细节；尽量少用命令行，改文件前说清会改什么。",
    };
}
