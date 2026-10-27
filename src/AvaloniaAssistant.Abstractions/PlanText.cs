namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 计划的文本格式。走「每行一条、方括号标状态」而不是 JSON 数组：
/// 模型写这种格式几乎不会错，工具的参数 schema 也不用为数组单独描述 items。
/// <code>
/// - [x] 查出下载目录里的大文件
/// - [~] 按类型归档
/// - [ ] 生成清单
/// </code>
/// </summary>
public static class PlanText
{
    /// <summary>一条计划最多多少步。再多就不叫计划了，叫清单。</summary>
    private const int MaxSteps = 20;

    public static List<PlanStep> Parse(string? text)
    {
        var steps = new List<PlanStep>();

        if (string.IsNullOrWhiteSpace(text))
        {
            return steps;
        }

        foreach (var raw in text.ReplaceLineEndings("\n").Split('\n'))
        {
            if (steps.Count >= MaxSteps)
            {
                break;
            }

            var line = raw.Trim();

            // 容忍各种列表前缀，模型不一定记得只写一种
            foreach (var prefix in (string[])["- ", "* ", "+ ", "• "])
            {
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    line = line[prefix.Length..].Trim();
                    break;
                }
            }

            if (line.Length == 0)
            {
                continue;
            }

            var status = PlanStepStatus.Pending;

            if (line.StartsWith('[') && line.IndexOf(']') is var close && close is > 1 and < 6)
            {
                var mark = line[1..close].Trim();

                status = mark switch
                {
                    "x" or "X" or "✓" or "√" => PlanStepStatus.Done,
                    "~" or "-" or ">" => PlanStepStatus.InProgress,
                    _ => PlanStepStatus.Pending,
                };

                line = line[(close + 1)..].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            steps.Add(new PlanStep { Text = line, Status = status });
        }

        return steps;
    }

    /// <summary>反过来写成同样的格式，导出会话时用。</summary>
    public static string Format(IEnumerable<PlanStep> steps)
    {
        var builder = new System.Text.StringBuilder();

        foreach (var step in steps)
        {
            var mark = step.Status switch
            {
                PlanStepStatus.Done => "x",
                PlanStepStatus.InProgress => "~",
                _ => " ",
            };

            builder.Append("- [").Append(mark).Append("] ").AppendLine(step.Text);
        }

        return builder.ToString().TrimEnd();
    }
}
