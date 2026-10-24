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
            window.Show();

            if (window.DataContext is MainWindowViewModel viewModel)
            {
                _ = InitializeAsync(viewModel, window, desktop);
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

            switch (AppLaunchOptions.Page)
            {
                case "chat":
                    viewModel.LoadDemoSidebar();
                    viewModel.LoadDemoChat();

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
