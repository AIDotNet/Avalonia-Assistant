using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace AvaloniaAssistant.UI.Services;

public interface IFilePicker
{
    Task<string?> PickFolderAsync(string title);

    Task<string?> PickFileAsync(string title);
}

public sealed class StorageFilePicker : IFilePicker
{
    public async Task<string?> PickFolderAsync(string title)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null)
        {
            return null;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<string?> PickFileAsync(string title)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null)
        {
            return null;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private static TopLevel? GetTopLevel()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

public static class Ui
{
    public static async Task OnUiAsync(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        await Dispatcher.UIThread.InvokeAsync(action);
    }

    public static void Post(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action);
    }

    public static async Task CopyTextAsync(string text)
    {
        var window = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        if (window?.Clipboard is not null)
        {
            await window.Clipboard.SetTextAsync(text);
        }
    }

    public static void OpenExternal(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true,
            });
        }
        catch
        {
            // 打不开链接不应影响应用。
        }
    }
}

/// <summary>仅供开发/自动化截图使用，由 Desktop 入口在启动前赋值。</summary>
public static class AppLaunchOptions
{
    public static string? ScreenshotPath { get; set; }

    public static string Page { get; set; } = "home";

    /// <summary>开发用：启动后自动发送一条消息（配合 --screenshot 做端到端验证）。</summary>
    public static string? RunPrompt { get; set; }
}
