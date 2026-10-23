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

    /// <summary><paramref name="pattern"/> 传 <c>*.zip</c> 这样的通配符时只显示匹配的文件。</summary>
    Task<string?> PickFileAsync(string title, string? pattern = null);
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

    public async Task<string?> PickFileAsync(string title, string? pattern = null)
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
            FileTypeFilter = string.IsNullOrWhiteSpace(pattern)
                ? null
                : [new FilePickerFileType("匹配的文件") { Patterns = [pattern] }],
        });

        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    private static TopLevel? GetTopLevel()
        => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}

public static class Ui
{
    /// <summary>把用户目录缩成 ~，避免路径把界面撑长。</summary>
    public static string ShortenPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return string.Empty;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(home) && path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
            ? "~" + path[home.Length..]
            : path;
    }

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

    /// <summary>开发用：预填并聚焦输入框，检查焦点态和 Enter 发送。</summary>
    public static string? ComposerText { get; set; }

    /// <summary>开发用：预填后模拟一次 Enter，验证发送链路。</summary>
    public static bool PressEnter { get; set; }

    /// <summary>开发用：MCP 页直接展开 JSON 导入面板并填入示例，配合 --screenshot 检查版式。</summary>
    public static bool OpenMcpImport { get; set; }

    /// <summary>开发用：启动后直接弹「完全访问」风险确认层，确认即切档，配合 --screenshot 看版式。</summary>
    public static bool ConfirmFullAccessDialog { get; set; }

    /// <summary>开发用：预置一条资料引用，检查引用标签的版式。</summary>
    public static string? ComposerMention { get; set; }
}
