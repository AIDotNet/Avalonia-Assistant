namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 行级文本比对。产物面板要回答的是「这个文件被改成什么样了」，
/// 不是「模型调了几次写入」——所以差异必须在写完之后立刻算出来，不能靠模型自己描述。
/// </summary>
public static class TextDiff
{
    /// <summary>逐行比对的规模上限。超过之后 LCS 表会吃掉几十兆内存，不值得。</summary>
    private const long MaxCells = 4_000_000;

    /// <summary>一次最多渲染多少行差异。再多用户也不会看。</summary>
    private const int MaxDiffLines = 400;

    public sealed record DiffResult(string Text, int Added, int Removed)
    {
        public static readonly DiffResult Empty = new(string.Empty, 0, 0);

        public bool HasChanges => Added > 0 || Removed > 0;
    }

    private enum OpKind
    {
        Equal,
        Delete,
        Insert,
    }

    private readonly record struct Op(OpKind Kind, string Line);

    /// <summary>
    /// 算出 before → after 的统一 diff。
    /// <paramref name="before"/> 为 null 表示文件原本不存在（全部算新增）。
    /// </summary>
    public static DiffResult Compute(string? before, string? after, int context = 3)
    {
        var left = SplitLines(before);
        var right = SplitLines(after);

        if (left.Length == right.Length && left.AsSpan().SequenceEqual(right))
        {
            return DiffResult.Empty;
        }

        // 大文件不做逐行比对：界面只需要知道「改了多少行」，硬算会把 UI 卡住
        if (left.LongLength * right.LongLength > MaxCells)
        {
            return new DiffResult(
                $"（文件较大：{left.Length} 行 → {right.Length} 行，未生成逐行对比）",
                Math.Max(0, right.Length - left.Length),
                Math.Max(0, left.Length - right.Length));
        }

        var ops = BuildOps(left, right);

        var added = 0;
        var removed = 0;
        foreach (var op in ops)
        {
            if (op.Kind == OpKind.Insert)
            {
                added++;
            }
            else if (op.Kind == OpKind.Delete)
            {
                removed++;
            }
        }

        return new DiffResult(Render(ops, context), added, removed);
    }

    private static string[] SplitLines(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            lines[i] = lines[i].TrimEnd('\r');
        }

        // 末尾换行会切出一个空元素，不是真的多一行
        if (lines.Length > 1 && lines[^1].Length == 0)
        {
            return lines[..^1];
        }

        return lines;
    }

    private static List<Op> BuildOps(string[] left, string[] right)
    {
        var width = right.Length + 1;
        var dp = new int[(left.Length + 1) * width];

        for (var i = left.Length - 1; i >= 0; i--)
        {
            for (var j = right.Length - 1; j >= 0; j--)
            {
                dp[i * width + j] = string.Equals(left[i], right[j], StringComparison.Ordinal)
                    ? dp[(i + 1) * width + j + 1] + 1
                    : Math.Max(dp[(i + 1) * width + j], dp[i * width + j + 1]);
            }
        }

        var ops = new List<Op>(left.Length + right.Length);
        int x = 0, y = 0;

        while (x < left.Length && y < right.Length)
        {
            if (string.Equals(left[x], right[y], StringComparison.Ordinal))
            {
                ops.Add(new Op(OpKind.Equal, left[x]));
                x++;
                y++;
            }
            else if (dp[(x + 1) * width + y] >= dp[x * width + y + 1])
            {
                ops.Add(new Op(OpKind.Delete, left[x]));
                x++;
            }
            else
            {
                ops.Add(new Op(OpKind.Insert, right[y]));
                y++;
            }
        }

        while (x < left.Length)
        {
            ops.Add(new Op(OpKind.Delete, left[x++]));
        }

        while (y < right.Length)
        {
            ops.Add(new Op(OpKind.Insert, right[y++]));
        }

        return ops;
    }

    /// <summary>
    /// 只渲染变更附近 <paramref name="context"/> 行，其余折叠成一行说明。
    /// 全文渲染等于把整个文件贴进面板，改动一行和重写全文看起来一样。
    /// </summary>
    private static string Render(List<Op> ops, int context)
    {
        var changed = new List<int>();
        for (var i = 0; i < ops.Count; i++)
        {
            if (ops[i].Kind != OpKind.Equal)
            {
                changed.Add(i);
            }
        }

        if (changed.Count == 0)
        {
            return string.Empty;
        }

        // 把相邻的变更窗口合并，避免每个改动都单独起一段
        var windows = new List<(int Start, int End)>();
        foreach (var index in changed)
        {
            var start = Math.Max(0, index - context);
            var end = Math.Min(ops.Count - 1, index + context);

            if (windows.Count > 0 && start <= windows[^1].End + 1)
            {
                windows[^1] = (windows[^1].Start, Math.Max(windows[^1].End, end));
            }
            else
            {
                windows.Add((start, end));
            }
        }

        // 每行在原文 / 新文里的行号，用来写 @@ 头
        var oldNo = new int[ops.Count];
        var newNo = new int[ops.Count];
        int oldCursor = 1, newCursor = 1;
        for (var i = 0; i < ops.Count; i++)
        {
            oldNo[i] = oldCursor;
            newNo[i] = newCursor;

            switch (ops[i].Kind)
            {
                case OpKind.Equal:
                    oldCursor++;
                    newCursor++;
                    break;
                case OpKind.Delete:
                    oldCursor++;
                    break;
                default:
                    newCursor++;
                    break;
            }
        }

        var output = new List<string>(Math.Min(ops.Count, MaxDiffLines) + windows.Count * 2);

        for (var w = 0; w < windows.Count; w++)
        {
            if (output.Count >= MaxDiffLines)
            {
                output.Add($"…（差异太多，只显示前 {MaxDiffLines} 行）");
                break;
            }

            var (start, end) = windows[w];

            var oldCount = 0;
            var newCount = 0;
            for (var i = start; i <= end; i++)
            {
                if (ops[i].Kind != OpKind.Insert)
                {
                    oldCount++;
                }

                if (ops[i].Kind != OpKind.Delete)
                {
                    newCount++;
                }
            }

            output.Add($"@@ -{oldNo[start]},{oldCount} +{newNo[start]},{newCount} @@");

            for (var i = start; i <= end && output.Count < MaxDiffLines; i++)
            {
                var prefix = ops[i].Kind switch
                {
                    OpKind.Insert => "+",
                    OpKind.Delete => "-",
                    _ => " ",
                };

                output.Add(prefix + Clamp(ops[i].Line));
            }

            // 两段之间空一行，否则接缝处会看成同一段
            if (w < windows.Count - 1 && output.Count < MaxDiffLines)
            {
                output.Add(string.Empty);
            }
        }

        return string.Join('\n', output);
    }

    /// <summary>
    /// 单行最长留多少字符。压缩成一行几万字的 JSON、base64 之类的文件，
    /// 一整行存进会话存档会把文件撑到几十兆，面板里也读不出任何信息。
    /// </summary>
    private const int MaxLineLength = 400;

    private static string Clamp(string line)
        => line.Length <= MaxLineLength ? line : line[..MaxLineLength] + "…";
}
