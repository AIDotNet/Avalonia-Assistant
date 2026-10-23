using System.Text.Json.Serialization;

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

    /// <summary>这一轮用户 @ 引用进来的文件。内容留快照，重试或第二天接着聊都是同一份上下文。</summary>
    public List<ChatAttachment> Attachments { get; set; } = [];

    /// <summary>这一轮用户粘贴进来的图片。</summary>
    public List<ChatImage> Images { get; set; } = [];

    public string? Error { get; set; }

    public string? Model { get; set; }

    public long InputTokens { get; set; }

    public long OutputTokens { get; set; }
}

/// <summary>
/// 一轮提问里引用的一份文件。只带模型真正需要的东西：
/// 展示用路径、正文快照、以及读取时有没有被截断。
/// </summary>
public sealed class ChatAttachment
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>展示与引用用的相对路径。</summary>
    public string? Path { get; set; }

    /// <summary>文件从哪来，目前只有 <c>workspace</c>。</summary>
    public string Source { get; set; } = "workspace";

    public string Kind { get; set; } = "FILE";

    public long Size { get; set; }

    /// <summary>正文快照，限长在读取时已经裁过；二进制文件为 null。</summary>
    public string? Text { get; set; }

    /// <summary>读取失败的原因。有值时正文为空，模型会看到「这个文件没读到」而不是一片空白。</summary>
    public string? Error { get; set; }

    public int LineCount { get; set; }

    /// <summary>是否只读了一部分（行数或字数到达上限）。</summary>
    public bool Truncated { get; set; }

    public bool HasText => !string.IsNullOrEmpty(Text);

    public string DisplayPath => string.IsNullOrWhiteSpace(Path) ? Name : Path!;
}

/// <summary>
/// 粘贴进对话的一张图片。二进制落在数据目录的 <c>images/</c> 下，会话只记路径和尺寸——
/// 把 base64 塞进 session.json 会让存档几十倍地涨。
/// </summary>
public sealed class ChatImage
{
    public string Id { get; set; } = string.Empty;

    /// <summary>PNG 文件的绝对路径。</summary>
    public string Path { get; set; } = string.Empty;

    public string MimeType { get; set; } = "image/png";

    public int Width { get; set; }

    public int Height { get; set; }

    public long Size { get; set; }

    [JsonIgnore]
    public string DisplaySize => Size >= 1024 * 1024
        ? $"{Size / 1024.0 / 1024.0:0.#} MB"
        : $"{Size / 1024.0:0.#} KB";

    [JsonIgnore]
    public string Caption => $"{Width}×{Height} · {DisplaySize}";
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
