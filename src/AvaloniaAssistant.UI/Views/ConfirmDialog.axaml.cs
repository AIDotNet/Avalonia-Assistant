using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AvaloniaAssistant.UI.Views;

/// <summary>
/// 通用二次确认：标题 + 一句话说明 + 两个按钮。
/// 只用在「不做就会丢东西」的动作上（重跑会删后续轮次、失效引用要不要照发），
/// 不给停止、删除这类已经有确认层的动作再加一层。
/// </summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>在 <paramref name="anchor"/> 所属窗口上弹出；返回 true 表示确认。</summary>
    public static async Task<bool> ShowAsync(
        Visual anchor,
        string title,
        string message,
        string confirmLabel,
        string cancelLabel = "取消",
        bool dangerous = false)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner)
        {
            return false;
        }

        var dialog = new ConfirmDialog();
        dialog.Apply(title, message, confirmLabel, cancelLabel, dangerous);

        // 和关窗确认、完全访问确认同一套：铺满父窗口，遮罩才像遮罩
        var origin = owner.PointToScreen(new Point(0, 0));
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = origin;
        dialog.Width = owner.ClientSize.Width;
        dialog.Height = owner.ClientSize.Height;

        return await dialog.ShowDialog<bool>(owner);
    }

    /// <summary>仅供截图：不挂模态，单独摆出来。</summary>
    public static ConfirmDialog CreatePreview(
        string title,
        string message,
        string confirmLabel,
        string cancelLabel = "取消",
        bool dangerous = false)
    {
        var dialog = new ConfirmDialog();
        dialog.Apply(title, message, confirmLabel, cancelLabel, dangerous);
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = new PixelPoint(40, 40);
        dialog.Width = 1000;
        dialog.Height = 640;
        return dialog;
    }

    private void Apply(string title, string message, string confirmLabel, string cancelLabel, bool dangerous)
    {
        if (this.FindControl<TextBlock>("TitleText") is { } titleText)
        {
            titleText.Text = title;
        }

        if (this.FindControl<TextBlock>("MessageText") is { } messageText)
        {
            messageText.Text = message;
        }

        if (this.FindControl<Button>("CancelButton") is { } cancel)
        {
            cancel.Content = cancelLabel;
        }

        if (this.FindControl<Button>("ConfirmButton") is { } confirm)
        {
            confirm.Content = confirmLabel;

            // danger 类自带红底，确认「删掉」时用它；其它确认沿用主按钮
            if (dangerous)
            {
                confirm.Classes.Remove("approve");
                confirm.Classes.Add("danger");
            }
        }
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

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(true);
}
