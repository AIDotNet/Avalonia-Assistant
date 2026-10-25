using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;

namespace AvaloniaAssistant.UI.Views;

/// <summary>
/// 快捷键总览。Ctrl+/ 打开，Esc 关闭。
/// 这里只列真实存在的键位：没实现的快捷键不往表里写，免得又变成假按钮。
/// </summary>
public partial class ShortcutsDialog : Window
{
    public ShortcutsDialog()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>铺满父窗口当遮罩用，和完全访问确认层、关窗确认层一致。</summary>
    public static async Task ShowAsync(Visual anchor)
    {
        if (TopLevel.GetTopLevel(anchor) is not Window owner)
        {
            return;
        }

        var dialog = new ShortcutsDialog();
        var origin = owner.PointToScreen(new Point(0, 0));
        dialog.WindowStartupLocation = WindowStartupLocation.Manual;
        dialog.Position = origin;
        dialog.Width = owner.ClientSize.Width;
        dialog.Height = owner.ClientSize.Height;

        await dialog.ShowDialog(owner);
    }

    /// <summary>开发用：不挂模态，单独摆出来截图。</summary>
    public static ShortcutsDialog CreatePreview() => new()
    {
        WindowStartupLocation = WindowStartupLocation.Manual,
        Position = new PixelPoint(40, 40),
        Width = 1000,
        Height = 640,
    };

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
