using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.Services;
using AvaloniaAssistant.UI.ViewModels;
using AvaloniaAssistant.UI.Views;
using Microsoft.Extensions.DependencyInjection;

namespace AvaloniaAssistant.UI;

public partial class AssistantApp : Application
{
    /// <summary>由 Desktop 入口在启动前赋值。</summary>
    public static IServiceProvider? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && Services is not null)
        {
            var window = Services.GetRequiredService<MainWindow>();
            desktop.MainWindow = window;

            if (window.DataContext is MainWindowViewModel viewModel)
            {
                // 先读设置再显示：窗口位置、尺寸和缩放要在第一帧就正确，
                // 否则会先居中弹出来再跳一下。LoadAsync 内部全用 ConfigureAwait(false)，同步等不会死锁。
                viewModel.SettingsProvider.LoadAsync().GetAwaiter().GetResult();
                window.ApplySavedWindowState();
            }

            window.Show();

            if (window.DataContext is MainWindowViewModel started)
            {
                _ = InitializeAsync(started, window, desktop);
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task InitializeAsync(
        MainWindowViewModel viewModel,
        Window window,
        IClassicDesktopStyleApplicationLifetime desktop)
    {
        try
        {
            await viewModel.InitializeAsync();

            if (!string.IsNullOrWhiteSpace(AppLaunchOptions.SessionQuery))
            {
                viewModel.SessionQuery = AppLaunchOptions.SessionQuery;
            }

            // 关窗确认平时只有真的关窗才出现，单独给一个截图入口
            if (AppLaunchOptions.RunningDialog)
            {
                var preview = RunningTasksDialog.CreatePreview(2);
                preview.Show();
                await Task.Delay(700);

                if (!string.IsNullOrWhiteSpace(AppLaunchOptions.ScreenshotPath))
                {
                    Capture(preview, AppLaunchOptions.ScreenshotPath);
                    preview.Close();
                    desktop.Shutdown();
                    return;
                }
            }

            // 快捷键总览：平时 Ctrl+/ 打开，这里给截图和人工验收一个固定入口
            if (AppLaunchOptions.ShowShortcuts)
            {
                var preview = ShortcutsDialog.CreatePreview();
                preview.Show();
                await Task.Delay(600);

                if (!string.IsNullOrWhiteSpace(AppLaunchOptions.ScreenshotPath))
                {
                    Capture(preview, AppLaunchOptions.ScreenshotPath);
                }

                preview.Close();

                if (!string.IsNullOrWhiteSpace(AppLaunchOptions.ScreenshotPath))
                {
                    desktop.Shutdown();
                    return;
                }
            }

            switch (AppLaunchOptions.Page)
            {
                case "chat":
                    viewModel.LoadDemoSidebar();
                    viewModel.LoadDemoChat(AppLaunchOptions.ChatDates);

                    if (viewModel.CurrentPage is ChatViewModel demoChat)
                    {
                        if (!string.IsNullOrWhiteSpace(AppLaunchOptions.ChatQueued))
                        {
                            // 连"正在生成"一起摆出来：排队条只在生成期间出现，单摆一条会看不到真实版式
                            demoChat.Composer.IsBusy = true;
                            demoChat.Composer.QueuedHint = "已排队：" + AppLaunchOptions.ChatQueued;
                        }

                        if (!string.IsNullOrWhiteSpace(AppLaunchOptions.ChatSearch))
                        {
                            demoChat.OpenSearchCommand.Execute(null);
                            demoChat.SearchQuery = AppLaunchOptions.ChatSearch;
                        }

                        if (AppLaunchOptions.ApprovalDemo)
                        {
                            demoChat.AddDemoApproval();
                        }
                    }

                    break;
                case "settings":
                    await viewModel.OpenSettingsAsync();
                    if (AppLaunchOptions.TestConnection && viewModel.CurrentPage is SettingsViewModel settingsPage)
                    {
                        await settingsPage.TestConnectionCommand.ExecuteAsync(null);
                    }

                    break;
                case "tools":
                    viewModel.OpenTools();
                    break;
                case "mcp":
                    viewModel.OpenMcp();
                    if (AppLaunchOptions.OpenMcpImport && viewModel.CurrentPage is McpViewModel mcp)
                    {
                        mcp.ShowImport();
                        mcp.FillExample();
                    }

                    break;
            }

            if (!string.IsNullOrWhiteSpace(AppLaunchOptions.RunPrompt))
            {
                await viewModel.StartNewChatAsync(AppLaunchOptions.RunPrompt);
            }

            // 验收用：真点一遍「换工作区 → 不使用工作空间 → 新建任务」。
            if (AppLaunchOptions.VerifyWorkspaceFlow)
            {
                await RunWorkspaceFlowAsync(viewModel);
            }

            if (!string.IsNullOrWhiteSpace(AppLaunchOptions.ComposerText))
            {
                viewModel.Home.Composer.Text = AppLaunchOptions.ComposerText;
                viewModel.Home.Composer.RequestFocus();
            }

            if (!string.IsNullOrWhiteSpace(AppLaunchOptions.ComposerMention))
            {
                await viewModel.Home.Composer.ReferenceByNameAsync(AppLaunchOptions.ComposerMention);
            }

            if (AppLaunchOptions.PressEnter)
            {
                await Task.Delay(800);
                var topLevel = TopLevel.GetTopLevel(window);
                if (topLevel?.FocusManager?.GetFocusedElement() is InputElement focused)
                {
                    focused.RaiseEvent(new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent,
                        Key = Key.Enter,
                        KeyModifiers = KeyModifiers.None,
                        Source = focused,
                    });
                }
            }

            // 验收用：对焦点控件发一次 Esc，检查审批卡是不是真的按「拒绝」处理。
            if (AppLaunchOptions.PressEscape)
            {
                await Task.Delay(700);
                var topLevel = TopLevel.GetTopLevel(window);
                if (topLevel?.FocusManager?.GetFocusedElement() is InputElement focused)
                {
                    focused.RaiseEvent(new KeyEventArgs
                    {
                        RoutedEvent = InputElement.KeyDownEvent,
                        Key = Key.Escape,
                        KeyModifiers = KeyModifiers.None,
                        Source = focused,
                    });
                }

                await Task.Delay(300);
            }

            // 验收用：直接弹「完全访问」确认层，确认后真的切到完全访问，配合 --screenshot 看结果。
            if (AppLaunchOptions.ConfirmFullAccessDialog)
            {
                var anchor = window.Content as Visual ?? window;
                if (await PermissionConfirmDialog.ShowAsync(anchor))
                {
                    await viewModel.Home.Composer.SetPermissionAsync(PermissionMode.FullAccess);
                }

                await Task.Delay(400);
            }

            // 验收用：套一个缩放值，检查界面放大后有没有挤压/裁切。
            if (AppLaunchOptions.FontScale is double scale)
            {
                viewModel.SetFontScale(scale, persist: false);
                await Task.Delay(250);
            }

            // 验收用：把主内容滚到底或滚到顶，长页面下半截 / 消息流开头才拍得到。
            if (AppLaunchOptions.ScrollToEnd || AppLaunchOptions.ScrollToTop)
            {
                // 刚切完页时 ScrollViewer 还没量出 Extent，等一下再滚，否则滚的是侧栏。
                await Task.Delay(350);

                var scroller = window.GetVisualDescendants()
                    .OfType<ScrollViewer>()
                    .Where(item => item.Extent.Height > item.Viewport.Height + 1)
                    .OrderByDescending(item => item.Viewport.Height)
                    .FirstOrDefault();

                if (scroller is not null)
                {
                    var offset = AppLaunchOptions.ScrollToEnd
                        ? scroller.Extent.Height - scroller.Viewport.Height
                        : 0;

                    scroller.Offset = new Vector(scroller.Offset.X, offset);
                    await Task.Delay(250);
                }
            }

            if (string.IsNullOrWhiteSpace(AppLaunchOptions.ScreenshotPath))
            {
                return;
            }

            // 验收用：打开真实的工作区浮层，让弹层自己出图——
            // 它挂在另一个 TopLevel 上，截主窗口看不到它。
            if (AppLaunchOptions.OpenWorkspaceFlyout)
            {
                await Task.Delay(400);
                var composer = window.GetVisualDescendants().OfType<ComposerView>().FirstOrDefault();
                var flyout = composer?.ShowWorkspaceFlyout();

                await Task.Delay(700);
                if (flyout?.Content is WorkspacePicker picker)
                {
                    picker.CaptureSelf(AppLaunchOptions.ScreenshotPath);
                }

                desktop.Shutdown();
                return;
            }

            // 验收用：工作区浮层挂在另一个 TopLevel 上，截主窗口看不到它，
            // 单独摆进一个无边框窗口再截，版式才是真实的。
            if (AppLaunchOptions.ShowWorkspacePicker)
            {
                await CaptureWorkspacePickerAsync(viewModel, AppLaunchOptions.ScreenshotPath);
                desktop.Shutdown();
                return;
            }

            var waitingForRun = !string.IsNullOrWhiteSpace(AppLaunchOptions.RunPrompt) || AppLaunchOptions.PressEnter;
            await Task.Delay(waitingForRun ? 4500 : 1600);

            // 拖放层要等页面真正进可视树之后再摆，否则 FindControl 还是空的
            if (AppLaunchOptions.ComposerDrop)
            {
                window.GetVisualDescendants()
                    .OfType<ComposerView>()
                    .FirstOrDefault()
                    ?.ShowDropPreview();
                await Task.Delay(250);
            }

            Capture(window, AppLaunchOptions.ScreenshotPath);
            desktop.Shutdown();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            viewModel.CurrentPage = viewModel.Home;

            if (!string.IsNullOrWhiteSpace(AppLaunchOptions.ScreenshotPath))
            {
                desktop.Shutdown(-1);
            }
        }
    }

    /// <summary>
    /// 验收「不使用工作空间」和「新建任务默认不带工作区」：走界面上的命令，不直接改设置。
    /// 最后故意从后台线程存一次设置——SaveAsync 内部全是 ConfigureAwait(false)，
    /// Changed 就是从那个线程上抛出来的，界面上崩的正是这一下。
    /// </summary>
    private static async Task RunWorkspaceFlowAsync(MainWindowViewModel viewModel)
    {
        var workspace = Path.Combine(AppPaths.Root, "verify-workspace");
        Directory.CreateDirectory(workspace);

        await viewModel.SelectWorkspaceAsync(workspace);
        Console.WriteLine($"[verify] 选好工作区：'{viewModel.Settings.WorkspacePath}'");
        Console.WriteLine($"[verify] 输入框显示：'{viewModel.Home.Composer.WorkspaceLabel}'");

        await viewModel.NewTaskCommand.ExecuteAsync(null);
        var chat = viewModel.CurrentPage as ChatViewModel;
        Console.WriteLine($"[verify] 新建任务带的工作区：'{chat?.Session.WorkspacePath}'");
        Console.WriteLine($"[verify] 新任务输入框显示：'{chat?.Composer.WorkspaceLabel}'");

        // 新任务还没开口就挑一个空间：这条任务得当场用上，不能等下一次新建
        await viewModel.SelectWorkspaceAsync(workspace);
        await Task.Delay(200);
        Console.WriteLine($"[verify] 挑完空间，任务用的目录：'{chat?.Session.WorkspacePath}'");

        var picker = new WorkspacePickerViewModel(viewModel);
        await picker.ClearWorkspaceCommand.ExecuteAsync(null);
        await Task.Delay(200);
        Console.WriteLine($"[verify] 不使用工作空间后：设置='{viewModel.Settings.WorkspacePath}' 任务='{chat?.Session.WorkspacePath}'");
        Console.WriteLine($"[verify] 输入框显示：'{chat?.Composer.WorkspaceLabel}' / 标题栏：'{chat?.HeaderSubtitle}'");

        // 设置变更事件是从保存所在的线程上抛的。从后台线程再存一次设置，
        // 界面（Models、SendCommand 的 CanExecuteChanged）能不能扛住，就看这一下。
        await Task.Run(() => viewModel.SettingsProvider.SaveAsync(viewModel.Settings));
        await Task.Delay(300);
        Console.WriteLine("[verify] 后台线程存设置：没抛异常");
    }

    private static async Task CaptureWorkspacePickerAsync(MainWindowViewModel viewModel, string path)
    {
        var picker = new WorkspacePickerViewModel(viewModel);

        if (AppLaunchOptions.WorkspacePickerCreate)
        {
            picker.StartCreateCommand.Execute(null);
        }
        else if (!string.IsNullOrWhiteSpace(AppLaunchOptions.WorkspacePickerQuery))
        {
            picker.SearchText = AppLaunchOptions.WorkspacePickerQuery;
        }

        var preview = new Window
        {
            WindowDecorations = WindowDecorations.None,
            SizeToContent = SizeToContent.WidthAndHeight,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(40, 40),
            Background = Brushes.White,
            Content = new WorkspacePicker { DataContext = picker },
        };

        preview.Show();
        await Task.Delay(700);
        Capture(preview, path);
        preview.Close();
    }

    private static void Capture(Window window, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 渲染窗口的视觉根（带背景的那一层），而不是 Window 本身：
        // Window 会让 RenderTargetBitmap 漏掉合成层（按钮、图标），
        // 只渲染 Content 又会在右下留下透明边。视觉根两者都能兼顾。
        var root = window.GetVisualChildren().OfType<Visual>().FirstOrDefault()
                   ?? (Visual?)window.Content
                   ?? window;

        var size = new PixelSize(
            Math.Max(1, (int)Math.Round(root.Bounds.Width)),
            Math.Max(1, (int)Math.Round(root.Bounds.Height)));

        var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(root);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
