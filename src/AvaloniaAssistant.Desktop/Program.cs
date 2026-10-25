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
        // 第一件事：本机 / 内网地址直连，别被系统代理（Clash 之类）接管。
        // 必须在任何 HttpClient 被创建之前。
        LocalNetworkProxy.Apply();

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
                    AppLaunchOptions.HasExplicitPage = true;
                    break;
                case "--data-dir" when index + 1 < args.Length:
                    // 验收/便携模式：把设置、会话和图片全放到指定目录，不碰真实数据。
                    AppPaths.UseRoot(args[++index]);
                    AppLaunchOptions.IsolatedData = true;
                    break;
                case "--run" when index + 1 < args.Length:
                    AppLaunchOptions.RunPrompt = args[++index];
                    break;
                case "--composer-text" when index + 1 < args.Length:
                    AppLaunchOptions.ComposerText = args[++index];
                    break;
                case "--search" when index + 1 < args.Length:
                    AppLaunchOptions.SessionSearch = args[++index];
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
                case "--test-connection":
                    AppLaunchOptions.TestConnection = true;
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
                case "--shortcuts":
                    AppLaunchOptions.ShowShortcuts = true;
                    break;
                case "--chat-dates":
                    AppLaunchOptions.ChatDates = true;
                    break;
                case "--chat-empty":
                    AppLaunchOptions.ChatEmpty = true;
                    break;
                case "--permission-pending":
                    AppLaunchOptions.PermissionPending = true;
                    break;
                case "--verify-models":
                    AppLaunchOptions.VerifyModels = true;
                    break;
                case "--model-flyout":
                    AppLaunchOptions.ModelFlyout = true;
                    break;
                case "--scroll-bottom":
                    AppLaunchOptions.ScrollToEnd = true;
                    break;
                case "--scroll-top":
                    AppLaunchOptions.ScrollToTop = true;
                    break;
                case "--approval-demo":
                    AppLaunchOptions.ApprovalDemo = true;
                    break;
                case "--approval-edited":
                    AppLaunchOptions.ApprovalDemo = true;
                    AppLaunchOptions.ApprovalEdited = true;
                    break;
                case "--press-escape":
                    AppLaunchOptions.PressEscape = true;
                    break;
                case "--approval-wait":
                    AppLaunchOptions.ApprovalWait = true;
                    break;
                case "--context-tight":
                    AppLaunchOptions.ContextTight = true;
                    break;
                case "--run-clock":
                    AppLaunchOptions.RunClock = true;
                    break;
                case "--demo-pin":
                    AppLaunchOptions.DemoPin = true;
                    break;
                case "--reading-ratio" when index + 1 < args.Length:
                    if (double.TryParse(
                            args[++index],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var ratio))
                    {
                        AppLaunchOptions.ReadingRatio = ratio;
                    }

                    break;
                case "--settings-invalid":
                    AppLaunchOptions.SettingsInvalid = true;
                    break;
                case "--demo-toast":
                    AppLaunchOptions.DemoToast = true;
                    break;
                case "--composer-hint":
                    AppLaunchOptions.ComposerHint = true;
                    break;
                case "--mcp-delete":
                    AppLaunchOptions.McpDelete = true;
                    break;
                case "--verify-back":
                    AppLaunchOptions.VerifyBack = true;
                    break;
                case "--verify-delete":
                    AppLaunchOptions.VerifyDelete = true;
                    break;
                case "--chat-search-close":
                    AppLaunchOptions.ChatSearchClose = true;
                    break;
                case "--verify-pin":
                    AppLaunchOptions.VerifyPin = true;
                    break;
                case "--settings-from-chat":
                    AppLaunchOptions.SettingsFromChat = true;
                    break;
                case "--verify-mcp":
                    AppLaunchOptions.VerifyMcp = true;
                    break;
                case "--composer-image" when index + 1 < args.Length:
                    AppLaunchOptions.ComposerImage = args[++index];
                    break;
                case "--verify-approval-bg":
                    AppLaunchOptions.VerifyApprovalBackground = true;
                    break;
                case "--verify-sidebar":
                    AppLaunchOptions.VerifySidebar = true;
                    break;
                case "--retry-confirm":
                    AppLaunchOptions.ShowRetryConfirm = true;
                    break;
                case "--scale" when index + 1 < args.Length:
                    if (double.TryParse(
                            args[++index],
                            System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture,
                            out var scale))
                    {
                        AppLaunchOptions.FontScale = scale;
                    }

                    break;
                case "--workspace-picker-create":
                    AppLaunchOptions.WorkspacePickerCreate = true;
                    break;
                case "--chat-search" when index + 1 < args.Length:
                    AppLaunchOptions.ChatSearch = args[++index];
                    break;
                case "--chat-queued" when index + 1 < args.Length:
                    AppLaunchOptions.ChatQueued = args[++index];
                    break;
                case "--session-query" when index + 1 < args.Length:
                    AppLaunchOptions.SessionQuery = args[++index];
                    break;
                case "--running-dialog":
                    AppLaunchOptions.RunningDialog = true;
                    break;
                case "--composer-drop":
                    AppLaunchOptions.ComposerDrop = true;
                    break;
                case "--workspace-flow":
                    AppLaunchOptions.VerifyWorkspaceFlow = true;
                    break;
                case "--tooltips":
                    AppLaunchOptions.ShowTooltips = true;
                    break;
            }
        }
    }
}
