using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _viewModel;

    public ChatView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += (_, _) => ScheduleInitialScroll();
    }

    private void ScheduleInitialScroll()
    {
        DispatcherTimer.RunOnce(() => ScrollToBottom(force: true), TimeSpan.FromMilliseconds(140));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        }

        _viewModel = DataContext as ChatViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
        }

        ScheduleInitialScroll();
        base.OnDataContextChanged(e);
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e)
        => Dispatcher.UIThread.Post(() => ScrollToBottom(force: false), DispatcherPriority.Background);

    private void ScrollToBottom(bool force)
    {
        var scroll = this.FindControl<ScrollViewer>("MessagesScroll");
        if (scroll is null)
        {
            return;
        }

        var distance = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
        if (force || distance < 180)
        {
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        }
    }
}
