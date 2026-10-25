using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace AvaloniaAssistant.UI.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += OnHomeAttached;
    }

    /// <summary>首页也整页接住拖放：落在建议卡片上时，用户并不会先去找输入框。</summary>
    private void OnHomeAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachDropSurface();
        Dispatcher.UIThread.Post(AttachDropSurface, DispatcherPriority.Loaded);
    }

    private void AttachDropSurface()
    {
        if (_dropSurfaceWired)
        {
            return;
        }

        if (this.GetVisualDescendants().OfType<ComposerView>().FirstOrDefault() is not { } composer)
        {
            return;
        }

        composer.AttachDropSurface(this);
        _dropSurfaceWired = true;
    }

    private bool _dropSurfaceWired;
}
