using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        AvaloniaXamlLoader.Load(this);
    }

    /// <summary>
    /// 进页面先回到顶部。输入框拿到焦点时会触发 BringIntoView，
    /// 之前偶尔一进来就停在中途（页头被滚没了），看着像页面自己跳了一下。
    /// </summary>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        Dispatcher.UIThread.Post(
            () => this.FindControl<ScrollViewer>("PageScroll")?.SetCurrentValue(ScrollViewer.OffsetProperty, default(Vector)),
            DispatcherPriority.Loaded);
    }

    /// <summary>切到完全访问要先过一次风险确认，和其它入口保持一致。</summary>
    private async void OnFullAccessClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel viewModel || viewModel.IsFullAccessSelected)
        {
            return;
        }

        if (!await PermissionConfirmDialog.ShowAsync(this))
        {
            return;
        }

        viewModel.SetPermissionCommand.Execute("full");
    }
}
