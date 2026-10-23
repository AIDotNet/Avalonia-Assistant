using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
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

            switch (AppLaunchOptions.Page)
            {
                case "chat":
                    viewModel.LoadDemoSidebar();
                    viewModel.LoadDemoChat();
                    break;
                case "settings":
                    await viewModel.OpenSettingsAsync();
                    break;
                case "tools":
                    viewModel.OpenTools();
                    break;
                case "library":
                    viewModel.OpenLibrary();
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

            if (string.IsNullOrWhiteSpace(AppLaunchOptions.ScreenshotPath))
            {
                return;
            }

            var waitingForRun = !string.IsNullOrWhiteSpace(AppLaunchOptions.RunPrompt) || AppLaunchOptions.PressEnter;
            await Task.Delay(waitingForRun ? 4500 : 1600);
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

    private static void Capture(Window window, string path)
    {
        // 位图的像素尺寸必须乘上系统缩放比例（例如 175%），否则渲染内容会被压到左上角、
        // 右侧与底部留出空白，看起来像"截图不居中"。
        var scaling = window.RenderScaling > 0 ? window.RenderScaling : 1.0;
        var size = new PixelSize(
            Math.Max(1, (int)Math.Round(window.Bounds.Width * scaling)),
            Math.Max(1, (int)Math.Round(window.Bounds.Height * scaling)));

        var bitmap = new RenderTargetBitmap(size, new Vector(96 * scaling, 96 * scaling));
        bitmap.Render(window);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}
