using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Storage;

/// <summary>
/// 会话搜索：标题、所属空间名、消息正文、工具结果。
/// 命中正文时给一句上下文，侧栏挂在标题下面——只显示标题的话，用户不知道它凭什么命中。
/// </summary>
public static class SessionSearch
{
    /// <summary>空词视为全通过；命中正文时 <paramref name="snippet"/> 是关键词前后的那一句。</summary>
    public static bool TryMatch(ChatSession session, string query, out string snippet)
    {
        snippet = string.Empty;
        if (string.IsNullOrWhiteSpace(query))
        {
            return true;
        }

        if (session.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
            || WorkspaceName(session).Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var entry in session.Entries)
        {
            if (TryMatchText(entry.Text, query, out snippet))
            {
                return true;
            }

            foreach (var tool in entry.ToolCalls)
            {
                if (TryMatchText(tool.Label, query, out snippet)
                    || TryMatchText(tool.Result, query, out snippet))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryMatchText(string? text, string query, out string snippet)
    {
        snippet = string.Empty;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var index = text.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            return false;
        }

        var start = Math.Max(0, index - 12);
        var end = Math.Min(text.Length, index + query.Length + 20);
        var slice = text[start..end].ReplaceLineEndings(" ").Trim();

        snippet = (start > 0 ? "…" : string.Empty)
                  + slice
                  + (end < text.Length ? "…" : string.Empty);
        return true;
    }

    private static string WorkspaceName(ChatSession session)
    {
        if (string.IsNullOrWhiteSpace(session.WorkspacePath))
        {
            return string.Empty;
        }

        return Path.GetFileName(session.WorkspacePath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar));
    }
}
