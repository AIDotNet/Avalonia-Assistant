using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _viewModel;
    private bool _autoScroll = true;
    private bool _scrolling;

    public ChatView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += (_, _) =>
        {
            var scroll = this.FindControl<ScrollViewer>("MessagesScroll");
            if (scroll is not null)
            {
                scroll.LayoutUpdated += (_, _) =>
                {
                    if (_autoScroll)
                    {
                        ScrollToBottom();
                    }
                };
                scroll.PointerWheelChanged += (_, args) =>
                {
                    if (args.Delta.Y > 0)
                    {
                        _autoScroll = false;
                    }
                    else
                    {
                        var distance = scroll.Extent.Height - scroll.Viewport.Height - scroll.Offset.Y;
                        _autoScroll = distance < 40;
                    }
                };
            }

            ScheduleInitialScroll();
        };
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void ScheduleInitialScroll()
    {
        _autoScroll = true;
        DispatcherTimer.RunOnce(ScrollToBottom, TimeSpan.FromMilliseconds(140));
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

    private void OnScrollToEndRequested(object? sender, bool force)
    {
        if (force)
        {
            _autoScroll = true;
        }

        if (!_autoScroll)
        {
            return;
        }

        Dispatcher.UIThread.Post(ScrollToBottom, DispatcherPriority.Background);
    }

    private void ScrollToBottom()
    {
        if (_scrolling)
        {
            return;
        }

        var scroll = this.FindControl<ScrollViewer>("MessagesScroll");
        if (scroll is null)
        {
            return;
        }

        _scrolling = true;
        try
        {
            scroll.Offset = new Vector(scroll.Offset.X, Math.Max(0, scroll.Extent.Height - scroll.Viewport.Height));
        }
        finally
        {
            _scrolling = false;
        }
    }
}
