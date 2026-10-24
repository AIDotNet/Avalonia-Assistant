using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using AvaloniaAssistant.UI.ViewModels;

namespace AvaloniaAssistant.UI.Views;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    /// <summary>用户已经在关窗确认里选了「停止并退出」，第二次 Close 不再拦。</summary>
    private bool _closeConfirmed;

    /// <summary>确认框开着的时候再点一次关闭，不要叠第二个。</summary>
    private bool _closeDialogOpen;

    public MainWindow(MainWindowViewModel viewModel)
    {
        AvaloniaXamlLoader.Load(this);
        DataContext = viewModel;
        _viewModel = viewModel;

        // 全局快捷键在窗口层收：输入框里打字时也能用，又不至于和 Textarea 自己的按键打架。
        AddHandler(KeyDownEvent, OnWindowKeyDown, RoutingStrategies.Tunnel);

        _viewModel.AttentionRequested += OnAttentionRequested;
        Closed += (_, _) => _viewModel.AttentionRequested -= OnAttentionRequested;
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

            case Key.F:
                // 只在对话页有意义：首页/设置页没有可查的消息流
                if (_viewModel.CurrentPage is ChatViewModel chat)
                {
                    chat.OpenSearchCommand.Execute(null);
                    e.Handled = true;
                }

                break;
        }
    }

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
    /// 关窗前如果还有任务在跑，先说清楚会中断什么。
    /// 以前这里直接退出，跑到一半的轮次连提示都没有。
    /// </summary>
    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_closeConfirmed
            || _closeDialogOpen
            || _viewModel.RunningChatCount == 0)
        {
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
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
            Close();
        }
        finally
        {
            _closeDialogOpen = false;
        }
    }

    // ---- 任务栏闪烁（Windows） ----

    private const uint FlashTray = 0x00000002;
    private const uint FlashUntilFocused = 0x0000000C;

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
}
