using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly DispatcherTimer _boundsTimer;
    private bool _restoringBounds;

    public MainWindow(MainWindowViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);

        _viewModel = viewModel;
        DataContext = viewModel;

        RestoreBounds();

        // 拖窗口时不用每一帧都写盘，停手 700ms 记一次。
        _boundsTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _boundsTimer.Tick += (_, _) =>
        {
            _boundsTimer.Stop();
            SaveBounds();
        };

        SizeChanged += (_, _) => QueueBoundsSave();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty)
            {
                QueueBoundsSave();
            }
        };

        // 桌面上这几下是肌肉记忆，缺了会觉得「不像本地应用」。
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.N, KeyModifiers.Control),
            Command = viewModel.NewTaskCommand,
        });
        KeyBindings.Add(new KeyBinding
        {
            Gesture = new KeyGesture(Key.OemComma, KeyModifiers.Control),
            Command = viewModel.OpenSettingsCommand,
        });

        // 走隧道阶段：焦点在输入框里时也要能收到。
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        _boundsTimer.Stop();
        SaveBounds();
        base.OnClosing(e);
    }

    private void QueueBoundsSave()
    {
        if (_restoringBounds)
        {
            return;
        }

        _boundsTimer.Stop();
        _boundsTimer.Start();
    }

    /// <summary>
    /// 恢复上次的尺寸。位置不记：多显示器拔掉之后窗口会跑到屏幕外，
    /// 每次居中显示反而更稳。
    /// </summary>
    private void RestoreBounds()
    {
        if (WindowBounds.TryRead() is not { } bounds)
        {
            return;
        }

        _restoringBounds = true;
        try
        {
            if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
            {
                var scaling = RenderScaling > 0 ? RenderScaling : 1;

                // 换了更小的屏幕时不要把窗口撑出可视区。
                var maxWidth = double.PositiveInfinity;
                var maxHeight = double.PositiveInfinity;
                if (Screens.Primary?.WorkingArea is { } area)
                {
                    maxWidth = area.Width / scaling;
                    maxHeight = area.Height / scaling;
                }

                var width = Math.Min(bounds.Width, maxWidth);
                var height = Math.Min(bounds.Height, maxHeight);
                if (width >= MinWidth && height >= MinHeight)
                {
                    Width = width;
                    Height = height;
                }
            }

            if (bounds.Maximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch (Exception)
        {
            // 记不住尺寸不该拦住启动，按默认值显示。
        }
        finally
        {
            _restoringBounds = false;
        }
    }

    private void SaveBounds()
    {
        // 截图/自动化跑完就退出，不把临时窗口尺寸写进用户数据目录。
        if (Services.AppLaunchOptions.ScreenshotPath is not null)
        {
            return;
        }

        // 最小化时的 Width/Height 不是用户想要的还原尺寸，别拿它覆盖已有记录。
        if (WindowState == WindowState.Minimized)
        {
            return;
        }

        if (WindowState == WindowState.Maximized)
        {
            // 最大化时 Width/Height 是铺满屏幕的值。没有历史尺寸就存 0，
            // 下次启动按默认尺寸打开再最大化，取消最大化时才不会得到一个满屏大的窗口。
            var previous = WindowBounds.TryRead();
            WindowBounds.Write(new WindowBounds(previous?.Width ?? 0, previous?.Height ?? 0, true));
            return;
        }

        if (double.IsNaN(Width) || double.IsNaN(Height))
        {
            return;
        }

        WindowBounds.Write(new WindowBounds(Width, Height, false));
    }

    private void OnWindowKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.K || !e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            return;
        }

        e.Handled = true;

        // 侧栏收起来时先展开，再把焦点放进搜索框。
        if (!_viewModel.IsSidebarVisible)
        {
            _viewModel.ToggleSidebarCommand.Execute(null);
        }

        var sidebar = this.FindControl<SidebarView>("Sidebar");
        Dispatcher.UIThread.Post(() => sidebar?.FocusSearch(), DispatcherPriority.Input);
    }
}
