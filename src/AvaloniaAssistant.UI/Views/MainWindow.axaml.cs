using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaAssistant.UI.Services;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    /// <summary>用户已经在关窗确认里选了「停止并退出」，第二次 Close 不再拦。</summary>
    private bool _closeConfirmed;

    /// <summary>
    /// 上一次跑完是成功还是失败，只用来挑提示音。
    /// 顺序上它总在 <c>AttentionRequested</c> 之前到：ChatViewModel 先报结束，再翻 IsBusy。
    /// </summary>
    private bool _lastRunFailed;

    /// <summary>确认框开着的时候再点一次关闭，不要叠第二个。</summary>
    private bool _closeDialogOpen;

    private bool _resizingSidebar;
    private double _resizeStartX;
    private double _resizeStartWidth;

    private DispatcherTimer? _windowStateTimer;
    private DispatcherTimer? _scaleSaveTimer;

    public MainWindow(MainWindowViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        _viewModel = viewModel;

        // 全局快捷键在窗口层收：输入框里打字时也能用，又不至于和 Textarea 自己的按键打架。
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        // Ctrl+滚轮缩放：放在隧道阶段，先于聊天区的滚动处理拿事件。
        AddHandler(PointerWheelChangedEvent, OnWindowPointerWheelChanged, RoutingStrategies.Tunnel);

        // 拖文件/图片进来：落点在整窗范围内接，见 MainWindow.Drop.cs
        InitializeDragDrop();

        _viewModel.AttentionRequested += OnAttentionRequested;
        _viewModel.FontScaleChanged += OnFontScaleChanged;
        _viewModel.RunFinished += OnRunFinished;

        PositionChanged += OnWindowPositionChanged;
        SizeChanged += OnWindowSizeChanged;

        Closed += (_, _) =>
        {
            _viewModel.AttentionRequested -= OnAttentionRequested;
            _viewModel.FontScaleChanged -= OnFontScaleChanged;
            _viewModel.RunFinished -= OnRunFinished;
            _windowStateTimer?.Stop();
            _scaleSaveTimer?.Stop();
        };
    }

    /// <summary>
    /// 启动时把上次的窗口位置、尺寸、界面缩放摆回去。
    /// 由 App 在 Show() 之前调用，否则会先居中再跳一下。
    /// </summary>
    public void ApplySavedWindowState()
    {
        // 截图模式用固定尺寸：文档配图不该跟着本机的窗口大小和缩放变。
        if (IsScreenshotRun)
        {
            return;
        }

        var settings = _viewModel.Settings;

        Width = Math.Max(MinWidth, settings.WindowWidth);
        Height = Math.Max(MinHeight, settings.WindowHeight);

        if (settings.WindowLeft is int left
            && settings.WindowTop is int top
            && Screens.ScreenFromPoint(new PixelPoint(left, top)) is not null)
        {
            // 只要求左上角还落在某块屏幕上：拔掉外接屏之后不要把窗口恢复到看不见的地方。
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(left, top);
        }

        if (settings.WindowMaximized)
        {
            WindowState = WindowState.Maximized;
        }

        ApplyFontScale();
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        switch (e.Key)
        {
            case Key.N:
                _viewModel.NewTaskCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.K:
                _viewModel.RequestSearch();
                e.Handled = true;
                break;

            case Key.OemComma:
                _viewModel.OpenSettingsCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.F:
                // 只在对话页有意义：首页/设置页没有可查的消息流
                if (_viewModel.CurrentPage is ChatViewModel chat)
                {
                    chat.OpenSearchCommand.Execute(null);
                    e.Handled = true;
                }

                break;

            // Ctrl+/：快捷键总览。数字键盘的除号也认，用户按哪个都行。
            case Key.OemQuestion:
            case Key.Divide:
                _ = ShortcutsDialog.ShowAsync(this);
                e.Handled = true;
                break;

            case Key.OemPlus:
            case Key.Add:
                ChangeFontScale(0.05);
                e.Handled = true;
                break;

            case Key.OemMinus:
            case Key.Subtract:
                ChangeFontScale(-0.05);
                e.Handled = true;
                break;

            case Key.D0:
            case Key.NumPad0:
                _viewModel.SetFontScale(1, persist: true);
                e.Handled = true;
                break;
        }
    }

    // ---- 界面缩放 ----

    private void OnWindowPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.Delta.Y == 0)
        {
            return;
        }

        ChangeFontScale(e.Delta.Y > 0 ? 0.05 : -0.05);
        e.Handled = true;
    }

    private void ChangeFontScale(double delta)
    {
        _viewModel.SetFontScale(_viewModel.FontScale + delta, persist: false);
        ScheduleScaleSave();
    }

    /// <summary>滚轮会连着触发几十次，停手半秒再写文件。</summary>
    private void ScheduleScaleSave()
    {
        _scaleSaveTimer?.Stop();
        _scaleSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _scaleSaveTimer.Tick += (_, _) =>
        {
            _scaleSaveTimer?.Stop();
            _scaleSaveTimer = null;
            _ = _viewModel.SaveSettingsQuietlyAsync();
        };
        _scaleSaveTimer.Start();
    }

    private void OnFontScaleChanged(object? sender, EventArgs e) => ApplyFontScale();

    private void ApplyFontScale()
    {
        if (this.FindControl<LayoutTransformControl>("RootTransform") is not { } root)
        {
            return;
        }

        var scale = _viewModel.FontScale;
        root.LayoutTransform = Math.Abs(scale - 1) < 0.001
            ? null
            : new ScaleTransform(scale, scale);
    }

    // ---- 窗口位置/尺寸 ----

    private void OnWindowPositionChanged(object? sender, PixelPointEventArgs e) => ScheduleWindowStateSave();

    private void OnWindowSizeChanged(object? sender, SizeChangedEventArgs e) => ScheduleWindowStateSave();

    /// <summary>截图/自动化跑批不该把临时窗口尺寸写回用户设置；隔离数据目录除外。</summary>
    private static bool IsScreenshotRun
        => !string.IsNullOrWhiteSpace(AppLaunchOptions.ScreenshotPath) && !AppLaunchOptions.IsolatedData;

    private void ScheduleWindowStateSave()
    {
        if (IsScreenshotRun)
        {
            return;
        }

        _windowStateTimer?.Stop();
        _windowStateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _windowStateTimer.Tick += (_, _) =>
        {
            _windowStateTimer?.Stop();
            _windowStateTimer = null;
            CaptureWindowState();
            _ = _viewModel.SaveSettingsQuietlyAsync();
        };
        _windowStateTimer.Start();
    }

    private void CaptureWindowState()
    {
        var settings = _viewModel.Settings;
        settings.WindowMaximized = WindowState == WindowState.Maximized;

        // 最大化时记的是「还原后的尺寸」：这时候 Bounds 是整块屏幕，存下去下次还原就回不来了。
        if (WindowState != WindowState.Normal)
        {
            return;
        }

        settings.WindowWidth = Math.Max(MinWidth, Bounds.Width);
        settings.WindowHeight = Math.Max(MinHeight, Bounds.Height);
        settings.WindowLeft = Position.X;
        settings.WindowTop = Position.Y;
    }

    // ---- 侧栏拖宽 ----

    private void OnSidebarResizerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control target)
        {
            return;
        }

        _resizingSidebar = true;
        _resizeStartX = e.GetPosition(this).X;
        _resizeStartWidth = _viewModel.SidebarWidth;
        e.Pointer.Capture(target);
        e.Handled = true;
    }

    private void OnSidebarResizerMoved(object? sender, PointerEventArgs e)
    {
        if (!_resizingSidebar)
        {
            return;
        }

        // 捕获丢了（切窗口、弹系统菜单）时不会再收到 Released，这里兜一次。
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            EndSidebarResize();
            return;
        }

        // 窗口坐标是缩放前的逻辑像素，侧栏宽度也是；界面放大后同样的鼠标位移要按比例折算。
        var delta = (e.GetPosition(this).X - _resizeStartX) / _viewModel.FontScale;
        _viewModel.SidebarWidth = _resizeStartWidth + delta;
        e.Handled = true;
    }

    private void OnSidebarResizerReleased(object? sender, PointerReleasedEventArgs e) => EndSidebarResize();

    private void EndSidebarResize()
    {
        if (!_resizingSidebar)
        {
            return;
        }

        _resizingSidebar = false;
        _ = _viewModel.PersistSidebarWidthAsync();
    }

    /// <summary>记下这一轮是成功还是失败，等 AttentionRequested 到了挑声音用。</summary>
    private void OnRunFinished(object? sender, ChatRunFinishedEventArgs e) => _lastRunFailed = e.Failed;

    /// <summary>
    /// 后台跑完一轮：窗口不在前台就闪一下任务栏。这不是系统通知，
    /// 但比"什么都不发生"强——用户切回来能看见任务栏在亮。
    /// </summary>
    private void OnAttentionRequested(object? sender, EventArgs e)
    {
        if (IsActive)
        {
            return;
        }

        FlashTaskbar();
        PlayAttentionSound();
    }

    /// <summary>
    /// 配一声提示音。只靠任务栏闪，用户在别的窗口里干活时根本注意不到；
    /// 失败和正常用两种声音，不用切回来就能判断要不要马上处理。
    /// </summary>
    private void PlayAttentionSound()
    {
        if (!OperatingSystem.IsWindows() || !_viewModel.Settings.PlayFinishSound)
        {
            return;
        }

        try
        {
            _ = MessageBeep(_lastRunFailed ? BeepError : BeepInformation);
        }
        catch (Exception)
        {
            // 没有声卡、被系统静音都不该影响任务收尾
        }
    }

    private void FlashTaskbar()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var info = new FlashWindowInfo
        {
            Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
            Window = handle,
            Flags = FlashTray | FlashUntilFocused,
            Count = 3,
        };

        _ = FlashWindowEx(ref info);
    }

    /// <summary>
    /// 关窗前如果还有任务在跑，先说清楚会中断什么；
    /// 然后记下窗口状态并同步落盘，再真的退出——异步等写入会被退出流程打断。
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeConfirmed && !_closeDialogOpen && _viewModel.RunningChatCount > 0)
        {
            e.Cancel = true;
            _ = ConfirmCloseAsync();
            return;
        }

        SaveWindowState();

        // 读完直接关窗：把当前任务的位置记下来，下次进来接着读
        _viewModel.PersistReadingPosition();

        base.OnClosing(e);
    }

    private async Task ConfirmCloseAsync()
    {
        _closeDialogOpen = true;
        try
        {
            var stopAndExit = await RunningTasksDialog.ShowAsync(this, _viewModel.RunningChatCount);
            if (!stopAndExit)
            {
                return;
            }

            _viewModel.StopAllRuns();
            _closeConfirmed = true;
            SaveWindowState();
            Close();
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    /// <summary>
    /// 同步写一次设置。SaveAsync 内部全部 ConfigureAwait(false)，
    /// 在 UI 线程上等待不会死锁；写的是几百字节的 JSON，阻塞可以忽略。
    /// </summary>
    private void SaveWindowState()
    {
        if (IsScreenshotRun)
        {
            return;
        }

        try
        {
            CaptureWindowState();
            _viewModel.SettingsProvider
                .SaveAsync(_viewModel.Settings)
                .GetAwaiter()
                .GetResult();
        }
        catch
        {
            // 关窗时保存失败不该阻止退出。
        }
    }

    // ---- 任务栏闪烁与提示音（Windows） ----

    private const uint FlashTray = 0x00000002;
    private const uint FlashUntilFocused = 0x0000000C;

    /// <summary>MB_ICONHAND：出错时用系统警示音。</summary>
    private const uint BeepError = 0x00000010;

    /// <summary>MB_ICONASTERISK：正常跑完用普通提示音。</summary>
    private const uint BeepInformation = 0x00000040;

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public IntPtr Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlashWindowEx(ref FlashWindowInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MessageBeep(uint type);
}
