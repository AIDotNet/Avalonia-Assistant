using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AvaloniaAssistant.UI.Views;

/// <summary>
/// 关窗时还有任务在跑：先说清楚会发生什么，再让用户选。
/// 以前这里直接退出，跑到一半的轮次连提示都没有。
/// </summary>
public partial class RunningTasksDialog : Window
{
    public RunningTasksDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>在 <paramref name="anchor"/> 所属的窗口上弹出，返回 true 表示「停止并退出」。</summary>
    public static async Task<bool> ShowAsync(Visual anchor, int runningCount)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner)
        {
            return false;
        }

        var dialog = new RunningTasksDialog();
        dialog.ApplyCount(runningCount);

        // 和完全访问确认层一样铺满父窗口，半透明背景才像遮罩
        var origin = owner.PointToScreen(new Point(0, 0));
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = origin;
        dialog.Width = owner.ClientSize.Width;
        dialog.Height = owner.ClientSize.Height;

        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>开发用：不挂模态，单独摆出来截图（关窗确认平时只有真的关窗才看得到）。</summary>
    public static RunningTasksDialog CreatePreview(int runningCount)
    {
        var dialog = new RunningTasksDialog();
        dialog.ApplyCount(runningCount);
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = new PixelPoint(40, 40);
        dialog.Width = 1000;
        dialog.Height = 640;
        return dialog;
    }

    private void ApplyCount(int runningCount)
    {
        if (this.FindControl<TextBlock>("MessageText") is not { } message)
        {
            return;
        }

        message.Text = runningCount == 1
            ? "现在关掉会中断正在执行的任务。已经生成的内容会保留，没跑完的这一轮不会继续。"
            : $"现在关掉会中断这 {runningCount} 个正在执行的任务。已经生成的内容会保留，没跑完的轮次不会继续。";
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close(false);
        }

        base.OnKeyDown(e);
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private void OnStopAndExitClick(object? sender, RoutedEventArgs e) => Close(true);
}
