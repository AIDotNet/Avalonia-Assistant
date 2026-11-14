namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 定时任务的日期 / 时间窗判定。纯函数，放在 Abstractions 里可单测；
/// 调度器（UI 层）只负责到点后去发起任务，不自己算日期。
/// </summary>
public static class ScheduleMath
{
    /// <summary>频率与有效期判定。</summary>
    public static bool MatchesDate(ScheduledTask task, DateOnly date)
    {
        var text = DateText(date);
        if (!string.IsNullOrWhiteSpace(task.ValidFrom)
            && string.CompareOrdinal(text, task.ValidFrom) < 0)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(task.ValidTo)
            && string.CompareOrdinal(text, task.ValidTo) > 0)
        {
            return false;
        }

        return task.Frequency switch
        {
            ScheduleFrequency.Once => string.Equals(task.OnceDate, text, StringComparison.Ordinal),
            ScheduleFrequency.Weekly => task.Weekdays.Count == 0
                ? (int)date.DayOfWeek == (int)task.CreatedAt.LocalDateTime.DayOfWeek
                : task.Weekdays.Contains((int)date.DayOfWeek),
            ScheduleFrequency.Monthly => task.MonthDays.Count == 0
                ? date.Day == task.CreatedAt.LocalDateTime.Day
                : task.MonthDays.Contains(date.Day),
            _ => true,
        };
    }

    /// <summary>在 (from, to] 这个时间窗里，这条任务有没有一次该触发的时刻。</summary>
    public static bool IsDueInWindow(ScheduledTask task, DateTimeOffset from, DateTimeOffset to)
    {
        if (!task.Enabled || task.Hour is < 0 or > 23 || task.Minute is < 0 or > 59)
        {
            return false;
        }

        var start = DateOnly.FromDateTime(from.LocalDateTime);
        var end = DateOnly.FromDateTime(to.LocalDateTime);
        for (var date = start; date <= end; date = date.AddDays(1))
        {
            if (!MatchesDate(task, date))
            {
                continue;
            }

            var candidate = new DateTime(
                date.Year, date.Month, date.Day, task.Hour, task.Minute, 0, DateTimeKind.Local);
            if (candidate > from.LocalDateTime && candidate <= to.LocalDateTime)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>下一次触发的展示文案。停用 / 单次已跑过 / 过了有效期都写清楚。</summary>
    public static string DescribeNextRun(ScheduledTask task)
    {
        if (!task.Enabled)
        {
            return task.Frequency == ScheduleFrequency.Once && task.LastRunAt is not null
                ? "已执行（单次）"
                : "已停用";
        }

        var time = $"{task.Hour:00}:{task.Minute:00}";
        return task.Frequency switch
        {
            ScheduleFrequency.Once => $"{task.OnceDate} {time} 单次",
            ScheduleFrequency.Weekly => $"每周 {time}",
            ScheduleFrequency.Monthly => $"每月 {time}",
            _ => $"每天 {time}",
        };
    }

    public static string DateText(DateOnly date) => date.ToString("yyyy-MM-dd");
}
