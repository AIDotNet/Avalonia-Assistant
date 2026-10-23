using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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

            if (string.IsNullOrWhiteSpace(AppLaunchOptions.ScreenshotPath))
            {
                return;
            }

            await Task.Delay(1600);
            Capture(window, AppLaunchOptions.ScreenshotPath);
            desktop.Shutdown();
        }
        catch (Exception ex)
        {
            if (!string.IsNullOrWhiteSpace(AppLaunchOptions.ScreenshotPath))
            {
                Console.Error.WriteLine(ex);
                desktop.Shutdown(-1);
            }
        }
    }

    private static void Capture(Window window, string path)
    {
        var size = new PixelSize(
            Math.Max(1, (int)window.Bounds.Width),
            Math.Max(1, (int)window.Bounds.Height));

        var bitmap = new RenderTargetBitmap(size, new Vector(96, 96));
        bitmap.Render(window);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        bitmap.Save(path);
    }
}
