using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;

namespace AvaloniaAssistant.UI.Controls;

/// <summary>
/// 对话里点开一张图之后铺在上面的查看层：滚轮缩放、拖动平移、Esc 关闭。
/// 只在需要时 <see cref="Show"/>，平时 <c>IsVisible=False</c>，不占布局也不吃事件。
/// </summary>
public partial class ImageViewer : UserControl
{
    private const double MinScale = 0.05;
    private const double MaxScale = 8;
    private const double Step = 1.25;

    // 遮罩上下有工具条和操作提示、左右留白：fit 只在这块带子里算，标签不会压到图角
    private const double ChromeTop = 56;
    private const double ChromeBottom = 40;
    private const double ChromeSide = 32;

    private double _scale = 1;
    private bool _dragging;
    private bool _wired;

    /// <summary>刚铺开还没量出视口尺寸：等布局落定后再算 fit 比例。</summary>
    private bool _fitPending;

    /// <summary>当前视口下「整张都看得见」的比例，用来判断操作提示该不该露。</summary>
    private double _fitScale = 1;

    /// <summary>用户自己缩放过。之后视口尺寸变化就别再改他的比例和位置。</summary>
    private bool _userZoomed;
    private Point _dragOrigin;
    private double _offsetX;
    private double _offsetY;
    private TopLevel? _topLevel;

    private readonly ScaleTransform _scaleTransform = new();
    private readonly TranslateTransform _translate = new();

    public ImageViewer()
    {
        // 必须走生成的 InitializeComponent：只有它会在 Load 之后把 x:Name 字段
        // （Preview / Viewport / CaptionText / ZoomText / HintText）填上。
        // 直接写 AvaloniaXamlLoader.Load(this) 只加载 XAML，字段全是 null，
        // Show() 里一碰就是空引用。
        InitializeComponent();

        EnsureWired();

        // 兜底：控件被重新挂到别的树上时再确认一次接线（幂等）
        Loaded += (_, _) => EnsureWired();
    }

    /// <summary>
    /// 接线：变换组、指针事件、尺寸变化。命名控件由 <see cref="InitializeComponent"/> 保证，
    /// 这里再判一次空只是为了把「控件没加载出来」当成静默失败，而不是崩在用户脸上。
    /// </summary>
    private void EnsureWired()
    {
        if (_wired || Preview is null || Viewport is null)
        {
            return;
        }

        _wired = true;

        // 先缩放再平移（TransformGroup 按声明顺序叠加），锚点换算见 SetScale
        Preview.RenderTransform = new TransformGroup { Children = { _scaleTransform, _translate } };

        Viewport.PointerPressed += OnViewportPointerPressed;
        Viewport.PointerMoved += OnViewportPointerMoved;
        Viewport.PointerReleased += OnViewportPointerReleased;
        Viewport.PointerCaptureLost += (_, _) => EndDrag();

        // 视口和图片谁先量出来都行：哪边变了就重摆一次。
        // 只挂 Viewport 不够——换一张尺寸不同的图时，视口尺寸没变，是图片自己在变。
        Viewport.SizeChanged += (_, _) => RefitIfUntouched();
        Preview.SizeChanged += (_, _) => RefitIfUntouched();
    }

    /// <summary>关闭之后触发，宿主可以据此把焦点还给对话。</summary>
    public event EventHandler? Closed;

    public bool IsOpen => IsVisible;

    /// <summary>遮罩弹出来。图放在 <paramref name="source"/>，副标题一般写尺寸和大小。</summary>
    public void Show(IImage source, string? caption = null)
    {
        EnsureWired();

        // 命名控件没拿到就什么都不做：宁可不弹，也不要抛空引用打断用户点图
        if (Preview is null || CaptionText is null || Viewport is null || ZoomText is null || HintText is null)
        {
            return;
        }

        Preview.Source = source;
        CaptionText.Text = caption ?? string.Empty;

        _dragging = false;
        _userZoomed = false;
        _fitPending = true;
        _scale = 1;
        _fitScale = 1;
        _scaleTransform.ScaleX = _scaleTransform.ScaleY = 1;
        _translate.X = 0;
        _translate.Y = 0;

        IsVisible = true;
        Place();
        UpdateChrome();

        // 布局要等这一帧走完才落定：那一刻的 Bounds 才是真的，再补一次
        Dispatcher.UIThread.Post(RefitIfUntouched, DispatcherPriority.Loaded);
        Focus();
    }

    public void Close()
    {
        if (!IsVisible)
        {
            return;
        }

        IsVisible = false;
        EndDrag();

        if (Preview is not null)
        {
            Preview.Source = null;
        }

        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 摆位置：按视口尺寸和图片的实际布局尺寸算 fit 比例与居中位移。
    /// <paramref name="refit"/> 或「还没做过首次 fit」时重算比例，否则只重摆、保留当前缩放。
    /// 视口还没量出来就先不管，等布局落定后的 SizeChanged / Post 再进来。
    /// </summary>
    private void Place(bool refit = false)
    {
        if (Preview is null || Viewport is null)
        {
            return;
        }

        var width = Viewport.Bounds.Width;
        var height = Viewport.Bounds.Height;
        if (width <= 0 || height <= 0)
        {
            // 还没量出来，等布局落定后 SizeChanged / Post 会再进来一次
            return;
        }

        // 用布局尺寸而不是位图尺寸：DPI 元数据、四舍五入都可能让两者差几个像素，
        // 差出来的这一截会直接表现成「没居中」。
        var image = LayoutSize(Preview);
        if (image.Width <= 0 || image.Height <= 0)
        {
            return;
        }

        if (refit || _fitPending)
        {
            _fitPending = false;
            _fitScale = FitScale(image, width, height);
            _scale = _fitScale;
            _scaleTransform.ScaleX = _scaleTransform.ScaleY = _scale;
        }

        // 顶部有尺寸标签、底部有操作提示，图就摆在两者之间的带子里，别让标签压在图角上
        var bandTop = ChromeTop;
        var bandHeight = Math.Max(1, height - ChromeTop - ChromeBottom);
        _translate.X = (width - image.Width * _scale) / 2;
        _translate.Y = bandTop + (bandHeight - image.Height * _scale) / 2;

        UpdateChrome();
    }

    /// <summary>图片在布局里的实际尺寸。还没排完版时 Bounds 是空的，退回位图自身的尺寸。</summary>
    private static Size LayoutSize(Image preview)
    {
        var bounds = preview.Bounds.Size;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            return bounds;
        }

        return preview.Source?.Size ?? default;
    }

    /// <summary>视口或图片尺寸变了就重摆；用户没自己缩放过，就顺手重新 fit。</summary>
    private void RefitIfUntouched()
    {
        if (!IsVisible || _dragging)
        {
            return;
        }

        Place(refit: !_userZoomed);
    }

    /// <summary>
    /// 「整张都看得见」的比例：左右各留 <see cref="ChromeSide"/>，上下让开工具条和提示，
    /// 小图不放大（不把 120px 的图糊成大块）。
    /// </summary>
    private static double FitScale(Size image, double viewportWidth, double viewportHeight)
    {
        if (image.Width <= 0 || image.Height <= 0)
        {
            return 1;
        }

        var width = Math.Max(1, viewportWidth - ChromeSide * 2);
        var height = Math.Max(1, viewportHeight - ChromeTop - ChromeBottom);
        var scale = Math.Min(width / image.Width, height / image.Height);

        return Math.Clamp(Math.Min(scale, 1), MinScale, MaxScale);
    }

    /// <summary>
    /// 以某个点为锚缩放，锚点在屏幕上的位置不动。
    /// 变换是「先缩放再平移」，锚点 c 满足 c = c·s + offset，换算过来就是这样。
    /// </summary>
    private void SetScale(double target, Point? anchor = null)
    {
        if (Viewport is null)
        {
            return;
        }

        var next = Math.Clamp(target, MinScale, MaxScale);
        if (Math.Abs(next - _scale) < 0.0001)
        {
            return;
        }

        var centerX = anchor?.X ?? Viewport.Bounds.Width / 2;
        var centerY = anchor?.Y ?? Viewport.Bounds.Height / 2;
        var factor = next / _scale;

        _translate.X = centerX - (centerX - _translate.X) * factor;
        _translate.Y = centerY - (centerY - _translate.Y) * factor;
        _scale = next;
        _scaleTransform.ScaleX = _scaleTransform.ScaleY = _scale;
        _userZoomed = true;

        UpdateChrome();
    }

    private void Reset()
    {
        // 硬回到 100% 对 4K 截图等于又跑到左上角去了；「适应窗口」才是还原的本意
        _userZoomed = false;
        _fitPending = true;
        Place();
    }

    /// <summary>正好「整张都看得见」时给一句操作提示，放大之后收起来，别一直压在图下面。</summary>
    private void UpdateChrome()
    {
        if (ZoomText is null || HintText is null)
        {
            return;
        }

        ZoomText.Text = $"{Math.Round(_scale * 100):0}%";
        HintText.IsVisible = Math.Abs(_scale - _fitScale) < 0.001;
    }

    private void EndDrag()
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (Viewport is not null)
        {
            Viewport.Cursor = new Cursor(StandardCursorType.Hand);
        }
    }

    private void OnViewportPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(Viewport);
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }

        _dragging = true;
        _dragOrigin = point.Position;
        _offsetX = _translate.X;
        _offsetY = _translate.Y;
        Viewport.Cursor = new Cursor(StandardCursorType.SizeAll);
        e.Pointer.Capture(Viewport);
        e.Handled = true;
    }

    private void OnViewportPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var position = e.GetPosition(Viewport);
        _translate.X = _offsetX + (position.X - _dragOrigin.X);
        _translate.Y = _offsetY + (position.Y - _dragOrigin.Y);
        e.Handled = true;
    }

    private void OnViewportPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        EndDrag();
        e.Pointer.Capture(null);
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (!IsVisible || e.Delta.Y == 0 || Viewport is null)
        {
            return;
        }

        SetScale(_scale * (e.Delta.Y > 0 ? Step : 1 / Step), e.GetPosition(Viewport));
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && IsVisible)
        {
            Close();
            e.Handled = true;
        }
    }

    // 控件自己不一定拿得到焦点（点到图上时焦点在别的元素），所以在窗口层兜一道 Esc。
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        EnsureWired();
        _topLevel = TopLevel.GetTopLevel(this);
        _topLevel?.AddHandler(KeyDownEvent, OnTopLevelKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_topLevel is not null)
        {
            _topLevel.RemoveHandler(KeyDownEvent, OnTopLevelKeyDown);
            _topLevel = null;
        }

        base.OnDetachedFromVisualTree(e);
    }

    private void OnTopLevelKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsVisible && !e.Handled)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnZoomInClick(object? sender, RoutedEventArgs e) => SetScale(_scale * Step);

    private void OnZoomOutClick(object? sender, RoutedEventArgs e) => SetScale(_scale / Step);

    private void OnResetClick(object? sender, RoutedEventArgs e) => Reset();

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
