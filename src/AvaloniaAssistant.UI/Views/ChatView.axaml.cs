using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

/// <summary>
/// 消息列表的滚动策略：输出时贴着底部跟随，但用户一旦碰滚动条、滚轮或键盘，
/// 必须立刻停手。之前每个 LayoutUpdated 都把 Offset 拉回底部，
/// 拖动过程中滑块每帧被拽回去，表现出来就是「滚动条拖不动」。
/// </summary>
public partial class ChatView : UserControl
{
    // 离底部小于这个距离，仍然算「在跟随输出」
    private const double FollowThreshold = 24;

    private ChatViewModel? _viewModel;
    private ScrollViewer? _scroll;
    private bool _followOutput = true;
    private bool _draggingScrollBar;
    private bool _scrollQueued;
    private double _lastExtent;
    private double _expectedOffsetY = double.NaN;

    private static readonly bool TraceEnabled = Environment.GetEnvironmentVariable("AA_SCROLL_TRACE") is not null;

    private static void Trace(string message)
    {
        if (!TraceEnabled)
        {
            return;
        }

        try
        {
            File.AppendAllText(
                Path.Combine(Path.GetTempPath(), "aa-scroll-trace.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // 诊断用，失败不影响界面
        }
    }

    public ChatView()
    {
        AvaloniaXamlLoader.Load(this);
        AttachedToVisualTree += OnAttachedToVisualTree;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
        }

        base.OnDetachedFromVisualTree(e);
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

    /// <summary>点一下用户贴的缩略图，铺开看大图。</summary>
    private void OnThumbnailPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Control { DataContext: Bitmap bitmap })
        {
            ImageViewerLayer.Show(bitmap, $"{bitmap.PixelSize.Width}×{bitmap.PixelSize.Height}");
            e.Handled = true;
        }
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        AttachScrollViewer();
        ScheduleInitialScroll();
    }

    private void AttachScrollViewer()
    {
        if (_scroll is not null)
        {
            return;
        }

        _scroll = this.FindControl<ScrollViewer>("MessagesScroll");
        if (_scroll is null)
        {
            return;
        }

        _scroll.LayoutUpdated += OnScrollLayoutUpdated;
        _scroll.ScrollChanged += OnScrollChanged;

        // Tunnel + handledEventsToo：滑块和 ScrollViewer 自己都会处理这些事件，
        // 走冒泡且默认忽略已处理事件的话根本收不到。
        _scroll.AddHandler(PointerPressedEvent, OnScrollPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(PointerReleasedEvent, OnScrollPointerReleased, RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(PointerCaptureLostEvent, OnScrollPointerCaptureLost, RoutingStrategies.Tunnel, handledEventsToo: true);
        _scroll.AddHandler(PointerWheelChangedEvent, OnScrollPointerWheelChanged, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>
    /// 只有内容真的变长了才跟随，避免每一趟布局都写 Offset。
    /// </summary>
    private void OnScrollLayoutUpdated(object? sender, EventArgs e)
    {
        if (_scroll is null)
        {
            return;
        }

        var extent = _scroll.Extent.Height;
        if (extent <= _lastExtent + 0.5)
        {
            return;
        }

        _lastExtent = extent;
        Trace($"layout extent={extent:0} offset={_scroll.Offset.Y:0} viewport={_scroll.Viewport.Height:0} follow={_followOutput} drag={_draggingScrollBar}");

        if (!_followOutput || _draggingScrollBar)
        {
            return;
        }

        ScrollToBottom();
    }

    /// <summary>
    /// 位置变了但不是我们写的，说明是用户在用键盘 / 触控板 / 拖滑块，立刻交出控制权。
    /// 只有「内容变长」不会走到这里（那种情况 OffsetDelta 为 0）。
    /// </summary>
    private void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_scroll is null || e.OffsetDelta.Y == 0)
        {
            return;
        }

        if (Math.Abs(_scroll.Offset.Y - _expectedOffsetY) < 1)
        {
            return;
        }

        if (_draggingScrollBar || !IsNearBottom())
        {
            Trace($"scrollchanged offset={_scroll.Offset.Y:0} delta={e.OffsetDelta.Y:0} -> follow=false (drag={_draggingScrollBar})");
            _followOutput = false;
            SyncFollowState();
        }
    }

    private void OnScrollPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!IsOwnScrollBar(e.Source as Visual))
        {
            return;
        }

        // 手一碰滚动条就交出控制权：拖动期间绝不能再写 Offset
        _draggingScrollBar = true;
        _followOutput = false;
        SyncFollowState();
    }

    private void OnScrollPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        EndScrollBarInteraction();
    }

    private void OnScrollPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        EndScrollBarInteraction();
    }

    private void EndScrollBarInteraction()
    {
        if (!_draggingScrollBar)
        {
            return;
        }

        _draggingScrollBar = false;
        // 拖回底部就继续跟随，拖到中间就停在那儿
        _followOutput = IsNearBottom();
        SyncFollowState();
        Trace($"drag end offset={_scroll?.Offset.Y:0} follow={_followOutput}");
    }

    private void OnScrollPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (_draggingScrollBar)
        {
            return;
        }

        if (e.Delta.Y > 0)
        {
            _followOutput = false;
            SyncFollowState();
        }
        else if (e.Delta.Y < 0 && IsNearBottom())
        {
            _followOutput = true;
            SyncFollowState();
        }
    }

    /// <summary>
    /// 判断被点中的滚动条是不是消息列表自己的（工具卡片里还有嵌套的 ScrollViewer）。
    /// </summary>
    private bool IsOwnScrollBar(Visual? source)
    {
        if (source is null || _scroll is null)
        {
            return false;
        }

        var bar = source.FindAncestorOfType<ScrollBar>(includeSelf: true);
        return bar is not null && ReferenceEquals(bar.FindAncestorOfType<ScrollViewer>(), _scroll);
    }

    private bool IsNearBottom()
    {
        if (_scroll is null)
        {
            return true;
        }

        var distance = _scroll.Extent.Height - _scroll.Viewport.Height - _scroll.Offset.Y;
        return distance <= FollowThreshold;
    }

    private void ScheduleInitialScroll()
    {
        _followOutput = true;
        _draggingScrollBar = false;
        _lastExtent = 0;
        _expectedOffsetY = double.NaN;
        SyncFollowState();
        DispatcherTimer.RunOnce(ScrollToBottom, TimeSpan.FromMilliseconds(140));
    }

    /// <summary>
    /// 把「在不在底部」告诉 VM。审批卡片要不要抢滚动、要不要飘「有新内容」，
    /// 都取决于这个状态；之前它只活在视图里，外面完全不知道。
    /// </summary>
    private void SyncFollowState()
    {
        if (_viewModel is null)
        {
            return;
        }

        _viewModel.IsAtBottom = _followOutput;
        if (_followOutput)
        {
            _viewModel.ClearFloatingHint();
        }
    }

    private void OnScrollToEndRequested(object? sender, bool force)
    {
        if (force && !_draggingScrollBar)
        {
            _followOutput = true;
            SyncFollowState();
        }

        Trace($"scrollrequest force={force} follow={_followOutput} drag={_draggingScrollBar}");
        if (!_followOutput || _draggingScrollBar)
        {
            return;
        }

        QueueScrollToBottom();
    }

    /// <summary>
    /// 流式输出每 55ms 请求一次滚动，这里合并成一次，别往 Dispatcher 里堆任务。
    /// </summary>
    private void QueueScrollToBottom()
    {
        if (_scrollQueued)
        {
            return;
        }

        _scrollQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _scrollQueued = false;
                ScrollToBottom();
            },
            DispatcherPriority.Background);
    }

    private void ScrollToBottom()
    {
        if (_scroll is null || !_followOutput || _draggingScrollBar)
        {
            return;
        }

        var target = Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height);
        if (Math.Abs(_scroll.Offset.Y - target) < 0.5)
        {
            return;
        }

        _expectedOffsetY = target;
        _scroll.Offset = new Vector(_scroll.Offset.X, target);
        Trace($"scrollto target={target:0} actual={_scroll.Offset.Y:0}");
    }
}
