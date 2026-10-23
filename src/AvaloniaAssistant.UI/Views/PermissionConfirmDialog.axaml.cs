using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;

namespace AvaloniaAssistant.UI.Views;

/// <summary>
/// 「完全访问」的风险确认。完全访问会把确认步骤全部关掉，所以切过去之前必须挡一下：
/// 列出被放开的能力，勾了「我已了解风险」才允许确认。
/// </summary>
/// <remarks>
/// 用独立 Window（ShowDialog）而不是 OverlayLayer：这个应用的 TopLevel 模板里没有
/// OverlayLayer（实测 GetOverlayLayer 返回 null），而遮罩层自己画又要处理 DPI 和层级，
/// 模态窗口更直接。
/// </remarks>
public partial class PermissionConfirmDialog : Window
{
    private bool _agreed;

    public PermissionConfirmDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>在 <paramref name="anchor"/> 所属的窗口上弹出确认，返回用户是否同意。</summary>
    public static async Task<bool> ShowAsync(Visual anchor)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner)
        {
            return false;
        }

        var dialog = new PermissionConfirmDialog();

        // 铺满父窗口：半透明背景才会像遮罩一样盖住整块客户区（窗口本身不可 resize，够用）。
        var origin = owner.PointToScreen(new Point(0, 0));
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = origin;
        dialog.Width = owner.ClientSize.Width;
        dialog.Height = owner.ClientSize.Height;

        return await dialog.ShowDialog<bool>(owner);
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

    private void OnAgreeClick(object? sender, RoutedEventArgs e)
    {
        _agreed = !_agreed;

        if (this.FindControl<Border>("AgreeBox") is { } box)
        {
            box.Background = _agreed
                ? Resource("AccentBrush", Brushes.Black)
                : Resource("SurfaceBrush", Brushes.White);
            box.BorderBrush = _agreed
                ? Resource("AccentBrush", Brushes.Black)
                : Resource("BorderStrongBrush", Brushes.Gainsboro);
        }

        if (this.FindControl<Control>("AgreeCheck") is { } check)
        {
            check.IsVisible = _agreed;
        }

        if (this.FindControl<Button>("ConfirmButton") is { } confirm)
        {
            confirm.IsEnabled = _agreed;
        }
    }

    private void OnConfirmClick(object? sender, RoutedEventArgs e) => Close(_agreed);

    private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(false);

    private static IBrush Resource(string key, IBrush fallback)
        => Application.Current?.FindResource(key) as IBrush ?? fallback;
}
