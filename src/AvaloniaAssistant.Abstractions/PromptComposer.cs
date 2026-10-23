using System.Text;

namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 把用户原话和 @ 引用的资料拼成真正发给模型的文本。
/// 界面气泡只显示原话，文档正文走这里——否则用户消息会被几千字的资料撑爆。
/// </summary>
public static class PromptComposer
{
    /// <summary>一次提问最多携带这么多字的资料，超出的部分截断并注明。</summary>
    public const int DefaultMaxAttachmentChars = 24000;

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
        builder.AppendLine("以下是我引用的资料，请当作已经读过的上下文，不用复述原文：");

        foreach (var attachment in attachments)
        {
            if (remaining <= 0)
            {
                builder.AppendLine("（其余资料因长度限制未展开）");
                break;
            }

            var body = BuildBody(attachment, remaining);
            remaining -= Math.Min(body.Length, remaining);

            builder.AppendLine($"<资料 名称=\"{attachment.Name}\">");
            builder.AppendLine(body);
            builder.AppendLine("</资料>");
        }

        return builder.ToString();
    }

    private static string BuildBody(ChatAttachment attachment, int budget)
    {
        if (!attachment.HasText)
        {
            return $"[{attachment.Kind} 文件，{attachment.Size} 字节，无法读取为文本]";
        }

        var text = attachment.Text!;
        if (text.Length <= budget)
        {
            return text;
        }

        // 截断位置落在半个 emoji / 代理对上会变成乱码，往回退一个字符
        var cut = budget;
        if (cut > 0 && char.IsHighSurrogate(text[cut - 1]))
        {
            cut--;
        }

        return text[..cut] + "\n（内容过长，已截断）";
    }
}
