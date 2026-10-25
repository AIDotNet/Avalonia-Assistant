using System.Text;

namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 把一个会话导出成 Markdown。纯函数，不碰磁盘、不依赖 Avalonia，方便单测。
/// 只带能看懂的部分：提问、回复、工具做过什么、哪里失败了；
/// 不导推理过程（那是模型的草稿，不是结论）。
/// </summary>
public static class SessionExport
{
    public static string ToMarkdown(ChatSession session)
    {
        var builder = new StringBuilder();
        var turns = session.Entries.Count(entry => entry.Kind == ChatEntryKind.User);

        builder.AppendLine($"# {session.Title}");
        builder.AppendLine();
        builder.AppendLine($"> {session.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} · {turns} 轮 · 导出 {DateTimeOffset.Now.LocalDateTime:yyyy-MM-dd HH:mm}");
        if (!string.IsNullOrWhiteSpace(session.WorkspacePath))
        {
            builder.AppendLine($"> 工作区：`{session.WorkspacePath}`");
        }

        builder.AppendLine();

        foreach (var entry in session.Entries)
        {
            switch (entry.Kind)
            {
                case ChatEntryKind.User:
                    builder.AppendLine("## 你");
                    builder.AppendLine();
                    builder.AppendLine(entry.Text);
                    builder.AppendLine();

                    if (entry.Attachments.Count > 0)
                    {
                        builder.AppendLine("引用：" + string.Join("、", entry.Attachments.Select(item => item.Name)));
                        builder.AppendLine();
                    }

                    break;

                case ChatEntryKind.Assistant:
                    var model = string.IsNullOrWhiteSpace(entry.Model) ? string.Empty : $"（{entry.Model}）";
                    builder.AppendLine($"## 助手{model}");
                    builder.AppendLine();
                    builder.AppendLine(string.IsNullOrWhiteSpace(entry.Text) ? "（没有正文）" : entry.Text);
                    builder.AppendLine();

                    if (entry.ToolCalls.Count > 0)
                    {
                        builder.AppendLine("工具调用：");
                        foreach (var tool in entry.ToolCalls)
                        {
                            var status = tool.IsError ? "失败" : tool.Aborted ? "已中断" : "完成";
                            var duration = tool.DurationMs is { } ms ? $" · {ms} ms" : string.Empty;
                            builder.AppendLine($"- {tool.Label}：{ToolTarget(tool.Arguments)}（{status}{duration}）");
                        }

                        builder.AppendLine();
                    }

                    if (!string.IsNullOrWhiteSpace(entry.Error))
                    {
                        builder.AppendLine("> 这一轮出错了：" + FirstLine(entry.Error));
                        builder.AppendLine();
                    }
                    else if (entry.Stopped)
                    {
                        builder.AppendLine("> 这一轮被你按停。");
                        builder.AppendLine();
                    }

                    break;

                case ChatEntryKind.Notice:
                    builder.AppendLine($"> {entry.Text}");
                    builder.AppendLine();
                    break;
            }
        }

        return builder.ToString().TrimEnd() + Environment.NewLine;
    }

    /// <summary>文件名里不能出现的字符换掉，标题本身可以原样留在正文第一行。</summary>
    public static string SuggestFileName(string title)
    {
        var name = string.IsNullOrWhiteSpace(title) ? "会话" : title.Trim();
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '-');
        }

        name = name.ReplaceLineEndings(" ").Trim();
        if (name.Length > 60)
        {
            name = name[..60];
        }

        return name.Length == 0 ? "会话" : name;
    }

    private static string ToolTarget(string arguments)
    {
        var line = FirstLine(arguments);
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    private static string FirstLine(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > 200 ? line[..200] + "…" : line;
    }
}
