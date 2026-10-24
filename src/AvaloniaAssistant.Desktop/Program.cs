using AtomUI;
using AtomUI.Desktop.Controls;
using Avalonia;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Agent;
using AvaloniaAssistant.Speech;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI;
using AvaloniaAssistant.UI.Services;
using AvaloniaAssistant.UI.ViewModels;
using AvaloniaAssistant.UI.Views;
using Microsoft.Extensions.DependencyInjection;
using TKCode.Arc;
using TKCode.Arc.Fonts;

namespace AvaloniaAssistant.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ParseArguments(args);
        AppPaths.EnsureCreated();

        var services = new ServiceCollection();
        services.AddSingleton<ISettingsProvider>(_ => new JsonSettingsProvider());
        services.AddSingleton<ISessionStore>(_ => new JsonSessionStore());
        services.AddSingleton<IFilePicker, StorageFilePicker>();
        services.AddSingleton<IVoiceInput, NAudioVoiceInput>();
        services.AddSingleton<WorkspaceFileSearch>();
        services.AddSingleton<IPiStreamFactory, PiStreamFactory>();
        services.AddSingleton<AgentToolRegistry>();
        services.AddSingleton<IAssistantRuntime, PiAgentRuntime>();
        services.AddSingleton<MainWindowViewModel>();
        services.AddSingleton<MainWindow>();

        AssistantApp.Services = services.BuildServiceProvider();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<AssistantApp>()
                  .UsePlatformDetect()
                  .WithInterFont()
                  .WithArcFonts()
                  .UseAtomUI(builder =>
                  {
                      builder.UseDesktopControls();
                      builder.UseArc();
                  })
                  .LogToTrace();

    private static void ParseArguments(string[] args)
    {
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--screenshot" when index + 1 < args.Length:
                    AppLaunchOptions.ScreenshotPath = args[++index];
                    break;
                case "--page" when index + 1 < args.Length:
                    AppLaunchOptions.Page = args[++index];
                    break;
                case "--run" when index + 1 < args.Length:
                    AppLaunchOptions.RunPrompt = args[++index];
                    break;
                case "--composer-text" when index + 1 < args.Length:
                    AppLaunchOptions.ComposerText = args[++index];
                    break;
                case "--confirm-full-access":
                    AppLaunchOptions.ConfirmFullAccessDialog = true;
                    break;
                case "--press-enter":
                    AppLaunchOptions.PressEnter = true;
                    break;
                case "--mcp-import":
                    AppLaunchOptions.OpenMcpImport = true;
                    break;
                case "--composer-mention" when index + 1 < args.Length:
                    AppLaunchOptions.ComposerMention = args[++index];
                    break;
                case "--workspace-picker":
                    AppLaunchOptions.ShowWorkspacePicker = true;
                    break;
                case "--workspace-flyout":
                    AppLaunchOptions.OpenWorkspaceFlyout = true;
                    break;
                case "--workspace-picker-query" when index + 1 < args.Length:
                    AppLaunchOptions.WorkspacePickerQuery = args[++index];
                    break;
                case "--workspace-picker-create":
                    AppLaunchOptions.WorkspacePickerCreate = true;
                    break;
                case "--data-dir" when index + 1 < args.Length:
                    AppPaths.UseRoot(args[++index]);
                    break;
            }
        }
    }
}
