namespace AvaloniaAssistant.Abstractions;

public enum ChatEntryKind
{
    User,
    Assistant,
    Notice,
}

public sealed class ChatSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Title { get; set; } = "新任务";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public string? WorkspacePath { get; set; }

    public string Mode { get; set; } = "office";

    public List<ChatEntry> Entries { get; set; } = [];
}

public sealed class ChatEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public ChatEntryKind Kind { get; set; }

    public string Text { get; set; } = string.Empty;

    public string? Reasoning { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;

    public List<ToolCallRecord> ToolCalls { get; set; } = [];

    /// <summary>这一轮用户从资料库引用进来的文档。内容留快照，重试或第二天接着聊都是同一份上下文。</summary>
    public List<ChatAttachment> Attachments { get; set; } = [];

    public string? Error { get; set; }

    public string? Model { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }
}

/// <summary>
/// 一轮提问里引用的一份资料。只带模型真正需要的东西，不含磁盘路径——
/// 会话存档换个工作区也还能用，不会指向一个已经不存在的位置。
/// </summary>
public sealed class ChatAttachment
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Kind { get; set; } = "FILE";

    public long Size { get; set; }

    /// <summary>正文快照，限长在导入时已经裁过；二进制文件为 null。</summary>
    public string? Text { get; set; }

    public bool HasText => !string.IsNullOrEmpty(Text);
}

public sealed class ToolCallRecord
{    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Label { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;

    public string? Result { get; set; }

    public bool IsError { get; set; }

    public long? DurationMs { get; set; }
}

public static class SessionText
{
    public static string BuildTitle(string prompt)
    {
        var line = prompt.ReplaceLineEndings(" ").Trim();
        if (line.Length == 0)
        {
            return "新任务";
        }

        return line.Length <= 22 ? line : line[..22] + "…";
    }

    public static string RelativeTime(DateTimeOffset value)
    {
        var span = DateTimeOffset.Now - value;
        if (span.TotalMinutes < 1)
        {
            return "刚刚";
        }

        if (span.TotalMinutes < 60)
        {
            return $"{(int)span.TotalMinutes} 分钟前";
        }

        if (span.TotalHours < 24 && value.Date == DateTimeOffset.Now.Date)
        {
            return value.ToString("HH:mm");
        }

        if (span.TotalDays < 2)
        {
            return "昨天 " + value.ToString("HH:mm");
        }

        return span.TotalDays < 7
            ? $"{(int)span.TotalDays} 天前"
            : value.ToString("MM月dd日");
    }
}
