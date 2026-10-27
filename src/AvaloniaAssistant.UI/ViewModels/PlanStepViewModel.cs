using Avalonia.Media;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>
/// 计划清单里的一步。三种状态各用一种图标和颜色：
/// 做完的打勾变灰，正在做的用琥珀色标出来，用户扫一眼就知道现在到哪了。
/// </summary>
public sealed class PlanStepViewModel
{
    private static readonly IBrush DoneBrush = new SolidColorBrush(Color.Parse("#9C9C94"));
    private static readonly IBrush ActiveBrush = new SolidColorBrush(Color.Parse("#A15C07"));
    private static readonly IBrush PendingBrush = new SolidColorBrush(Color.Parse("#6F6F69"));

    /// <summary>正在做的那一步用正文色，比待办更重，扫一眼就知道现在到哪。</summary>
    private static readonly IBrush CurrentTextBrush = new SolidColorBrush(Color.Parse("#1D1D1B"));

    public PlanStepViewModel(PlanStep step, int index)
    {
        Text = step.Text;
        Status = step.Status;
        Index = index;
        Number = index + 1;
    }

    public string Text { get; }

    public PlanStepStatus Status { get; }

    public int Index { get; }

    public int Number { get; }

    public bool IsDone => Status == PlanStepStatus.Done;

    public bool IsCurrent => Status == PlanStepStatus.InProgress;

    public bool IsPending => Status == PlanStepStatus.Pending;

    public string StatusText => Status switch
    {
        PlanStepStatus.Done => "已完成",
        PlanStepStatus.InProgress => "进行中",
        _ => "待办",
    };

    /// <summary>做完的步骤划一道线，让剩下的更显眼。</summary>
    public TextDecorationCollection? Decorations => IsDone ? TextDecorations.Strikethrough : null;

    public IBrush Foreground => IsDone
        ? DoneBrush
        : IsCurrent ? CurrentTextBrush : PendingBrush;

    public IBrush GlyphBrush => Status switch
    {
        PlanStepStatus.Done => DoneBrush,
        PlanStepStatus.InProgress => ActiveBrush,
        _ => PendingBrush,
    };
}
