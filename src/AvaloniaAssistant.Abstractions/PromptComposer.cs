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
        int maxTotalChars = DefaultMaxAttachmentChars)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return text;
        }

        var remaining = maxTotalChars;

        var builder = new StringBuilder();
        builder.AppendLine(text);
        builder.AppendLine();
        builder.AppendLine(ReminderOpen);
        builder.AppendLine("用户在这条消息里引用了下面的文件，应用已在发送前读过内容。");
        builder.AppendLine("请直接把它们当作已提供的上下文，不要再去搜索或重复读取同一路径。");
        builder.AppendLine("<selected_files>");

        foreach (var attachment in attachments)
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
