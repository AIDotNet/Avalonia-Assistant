using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.UI.Services;
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

        // 审批卡的键盘回答走隧道：输入框在同一个页面里，先于它拿到按键。
        AddHandler(KeyDownEvent, OnChatKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
            _viewModel.ScrollToMessageRequested -= OnScrollToMessageRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
            _viewModel.ScrollToMessageRequested -= OnScrollToMessageRequested;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = DataContext as ChatViewModel;
        if (_viewModel is not null)
        {
            _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
            _viewModel.ScrollToMessageRequested += OnScrollToMessageRequested;
            _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            // 重跑会删掉后面几轮，先把话说清楚再动手
            _viewModel.ConfirmAsync = (title, message, confirmLabel, dangerous) =>
                ConfirmDialog.ShowAsync(this, title, message, confirmLabel, "取消", dangerous);
        }

        ScheduleInitialScroll();
        base.OnDataContextChanged(e);
    }

    /// <summary>点一下引用标签：在资源管理器里定位这个文件，找不到就退到上级目录。</summary>
    private void OnAttachmentClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ChatAttachment attachment })
        {
            Ui.RevealInExplorer(attachment.FullPath ?? attachment.Path);
            e.Handled = true;
        }
    }

    /// <summary>
    /// 审批卡的键盘回答：Esc 拒绝（安全默认），Ctrl+Enter 允许一次，Ctrl+Shift+Enter 本会话都允许。
    /// 查找框、@ 浮层、录音各自有 Esc 语义，先让它们处理，别抢。
    /// </summary>
    private void OnChatKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null || _viewModel.IsSearchOpen)
        {
            return;
        }

        var composer = _viewModel.Composer;
        if (composer.ShowMention || composer.IsRecording)
        {
            return;
        }

        if (e.Key == Key.Escape)
        {
            if (_viewModel.TryAnswerApproval(allow: false, always: false))
            {
                e.Handled = true;
            }

            return;
        }

        if (e.Key == Key.Enter && e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            if (_viewModel.TryAnswerApproval(
                    allow: true,
                    always: e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
            {
                e.Handled = true;
            }
        }
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
        AttachSearchInput();
        AttachDropSurface();
        Dispatcher.UIThread.Post(AttachDropSurface, DispatcherPriority.Loaded);
        ScheduleInitialScroll();
    }

    /// <summary>把整页变成拖放落点：拖到消息区中间时不用先去找输入框。</summary>
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

    /// <summary>
    /// 查找框的按键要在隧道阶段截：arc:Input 内部的 TextBox 自己也会吃 Enter。
    /// </summary>
    private void AttachSearchInput()
    {
        if (_searchWired || this.FindControl<Control>("SearchInput") is not { } search)
        {
            return;
        }

        search.AddHandler(KeyDownEvent, OnSearchKeyDown, RoutingStrategies.Tunnel);
        _searchWired = true;
    }

    private bool _searchWired;

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (_viewModel is null)
        {
            return;
        }

        switch (e.Key)
        {
            case Key.Enter when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                _viewModel.NextSearchHitCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Enter:
                _viewModel.PreviousSearchHitCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Escape:
                _viewModel.CloseSearchCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    /// <summary>查找条打开、或者从一个命中跳到另一个时，把键盘交给查找框。</summary>
    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ChatViewModel.IsSearchOpen) || _viewModel?.IsSearchOpen != true)
        {
            return;
        }

        Dispatcher.UIThread.Post(
            () => this.FindControl<Control>("SearchInput")?.Focus(),
            DispatcherPriority.Input);
    }

    /// <summary>把命中的那条消息滚进视野。容器还没生成时等一趟布局。</summary>
    private void OnScrollToMessageRequested(object? sender, MessageViewModel message)
    {
        if (this.FindControl<ItemsControl>("MessagesItems") is not { } items)
        {
            return;
        }

        if (items.ContainerFromItem(message) is { } container)
        {
            container.BringIntoView();
            return;
        }

        Dispatcher.UIThread.Post(
            () => items.ContainerFromItem(message)?.BringIntoView(),
            DispatcherPriority.Loaded);
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
