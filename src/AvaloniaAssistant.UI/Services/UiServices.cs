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

    /// <summary>多选。返回空数组表示用户取消了。</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title, string? pattern = null);
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

    public async Task<IReadOnlyList<string>> PickFilesAsync(string title, string? pattern = null)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null)
        {
            return [];
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = true,
            FileTypeFilter = string.IsNullOrWhiteSpace(pattern)
                ? null
                : [new FilePickerFileType("匹配的文件") { Patterns = [pattern] }],
        });

        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToList();
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

    /// <summary>开发用：进设置页后立刻点一次「测试连接」，用来在命令行验收真实配置。</summary>
    public static bool TestConnection { get; set; }

    /// <summary>
    /// 开发用：把工作区浮层单独摆进一个窗口再截图。浮层挂在另一个 TopLevel 上，
    /// 直接截主窗口是看不到它的。
    /// </summary>
    public static bool ShowWorkspacePicker { get; set; }

    /// <summary>开发用：在主窗口里真的打开工作区浮层，让弹层自己出图。</summary>
    public static bool OpenWorkspaceFlyout { get; set; }

    /// <summary>开发用：工作区浮层截图时先填一段搜索词，检查过滤后的版式。</summary>
    public static string? WorkspacePickerQuery { get; set; }

    /// <summary>开发用：工作区浮层截图时直接切到「新建」态。</summary>
    public static bool WorkspacePickerCreate { get; set; }

    /// <summary>开发用：对话页打开会话内查找并填好关键词，检查查找条与命中高亮。</summary>
    public static string? ChatSearch { get; set; }

    /// <summary>开发用：对话页摆一条"已排队"提示，检查排队状态条。</summary>
    public static string? ChatQueued { get; set; }

    /// <summary>开发用：给侧栏搜索框预置关键词，检查正文命中摘要。</summary>
    public static string? SessionQuery { get; set; }

    /// <summary>开发用：单独摆出「还有任务在跑」的关窗确认层，配合 --screenshot 看版式。</summary>
    public static bool RunningDialog { get; set; }

    /// <summary>开发用：把输入框的拖放提示层摆出来，检查它盖住的区域和文案。</summary>
    public static bool ComposerDrop { get; set; }
}
