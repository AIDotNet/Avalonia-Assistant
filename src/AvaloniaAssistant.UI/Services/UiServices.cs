using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AvaloniaAssistant.Storage.Documents;

namespace AvaloniaAssistant.UI.Services;

public interface IFilePicker
{
    Task<string?> PickFolderAsync(string title);

    /// <summary><paramref name="pattern"/> 传 <c>*.zip</c> 这样的通配符时只显示匹配的文件。</summary>
    Task<string?> PickFileAsync(string title, string? pattern = null);

    /// <summary>多选。返回空数组表示用户取消了。</summary>
    Task<IReadOnlyList<string>> PickFilesAsync(string title, string? pattern = null);

    /// <summary>另存为。扩展名不带点，例如 <c>md</c>。</summary>
    Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension);
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

    public async Task<string?> PickSaveFileAsync(string title, string suggestedName, string extension)
    {
        var topLevel = GetTopLevel();
        if (topLevel is null)
        {
            return null;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType("Markdown") { Patterns = ["*.md"] }],
        });

        return file?.TryGetLocalPath();
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

    /// <summary>能在应用内预览的扩展名。不在这里的一律走系统程序。</summary>
    private static readonly HashSet<string> PreviewableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".docx", ".xlsx", ".pptx", ".pdf",
        ".md", ".markdown", ".txt", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml",
        ".cs", ".js", ".ts", ".py", ".html", ".htm", ".css", ".sql", ".log",
    };

    /// <summary>预览最多读多少字符。再长界面也读不完，让人打开原文件更实际。</summary>
    private const int PreviewMaxChars = 60_000;

    public static bool CanPreview(string? path)
        => !string.IsNullOrWhiteSpace(path) && PreviewableExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 在应用内预览文档。解析走 <c>Storage.Documents</c>，和 <c>read_document</c> 工具同一套，
    /// 所以 docx 的标题层级与表格、xlsx 的每张工作表、pdf 的按页正文都能保留。
    /// 打不开时退回系统程序——点了没反应比弹出去更让人困惑。
    /// </summary>
    public static async Task<bool> PreviewDocumentAsync(string path)
    {
        if (!File.Exists(path) || !CanPreview(path))
        {
            return false;
        }

        string content;
        try
        {
            content = await DocumentRead
                .ReadAsync(path, PreviewMaxChars, CancellationToken.None)
                .ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return OpenExternal(path);
        }

        var owner = (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
        var preview = new Views.DocumentPreviewWindow(path, content, content.Length >= PreviewMaxChars);

        if (owner is not null)
        {
            await preview.ShowDialog(owner);
        }
        else
        {
            preview.Show();
        }

        return true;
    }

    /// <summary>
    /// 用系统默认程序打开文件 / 目录 / 链接。打不开返回 false——
    /// 静默吞掉失败的话，用户点了「打开目录」只会觉得按钮坏了。
    /// </summary>
    public static bool OpenExternal(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return false;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target)
            {
                UseShellExecute = true,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 在资源管理器里定位一个文件；目录就直接打开，路径不在了就退到它的上级目录。
    /// 打不开不算错误——用户只是没看到那一下跳转。
    /// </summary>
    /// <summary>
    /// 在资源管理器里定位文件或目录。打不开返回 false，交给调用方提示——
    /// 静默失败会让「打开位置」看起来像坏按钮。
    /// </summary>
    public static bool RevealInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                if (File.Exists(path))
                {
                    StartExplorer($"/select,\"{path}\"");
                    return true;
                }

                if (Directory.Exists(path))
                {
                    StartExplorer($"\"{path}\"");
                    return true;
                }

                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent))
                {
                    StartExplorer($"\"{parent}\"");
                    return true;
                }

                return false;
            }

            return OpenExternal(File.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        }
        catch
        {
            return false;
        }
    }

    private static void StartExplorer(string arguments)
        => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", arguments)
        {
            UseShellExecute = false,
        });
}

/// <summary>仅供开发/自动化截图使用，由 Desktop 入口在启动前赋值。</summary>
public static class AppLaunchOptions
{
    public static string? ScreenshotPath { get; set; }

    public static string Page { get; set; } = "home";

    /// <summary>命令行是否显式指定了页面。显式指定时不再恢复上次会话，截图和验收才有稳定结果。</summary>
    public static bool HasExplicitPage { get; set; }

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

    /// <summary>开发用：开一个还没说过话的新任务，检查空状态版式。</summary>
    public static bool ChatEmpty { get; set; }

    /// <summary>开发用：摆出「生成中切了权限」的状态，检查 pill 上的「下一轮生效」角标。</summary>
    public static bool PermissionPending { get; set; }

    /// <summary>开发用：真的打开模型浮层并出图，检查当前项勾选、分组和说明。</summary>
    public static bool ModelFlyout { get; set; }

    /// <summary>
    /// 开发用：真打开工具权限浮层，点一次窗口别处、再按一次 Esc，验收「点外面收起」。
    /// 浮层是自己装配的，忘了挂 FlyoutDismiss 编译不会报错，只能这么验。
    /// </summary>
    public static bool VerifyFlyoutDismiss { get; set; }

    /// <summary>开发用：给侧栏搜索框预置关键词，检查正文命中摘要。</summary>
    public static string? SessionQuery { get; set; }

    /// <summary>开发用：把悬停提示单独摆进一个窗口再截图，检查提示框的底色/边框/折行。</summary>
    public static bool ShowTooltips { get; set; }

    /// <summary>开发用：单独摆出「还有任务在跑」的关窗确认层，配合 --screenshot 看版式。</summary>
    public static bool RunningDialog { get; set; }

    /// <summary>开发用：把输入框的拖放提示层摆出来，检查它盖住的区域和文案。</summary>
    public static bool ComposerDrop { get; set; }

    /// <summary>
    /// 开发用：把文件真灌进拖放管线，验证「拖上去有没有反应、落下有没有加进来」。
    /// 只跳过 OLE 投递，命中、AllowDrop、事件路由和后续处理都是真的。
    /// </summary>
    public static List<string> DropProbePaths { get; } = [];

    /// <summary>
    /// 开发用：把「换工作区 → 不使用工作空间 → 新建任务」这条链路真跑一遍。
    /// 走的是界面上那两个命令（ClearWorkspaceCommand / NewTaskCommand），不是直接改设置。
    /// </summary>
    public static bool VerifyWorkspaceFlow { get; set; }

    /// <summary>
    /// 开发用：把「加一套模型 → 设为当前 → 保存 → 重新读盘 → 输入框切换 → 删一套」真跑一遍。
    /// 走的是设置页上的命令，不是直接改 JSON。
    /// </summary>
    public static bool VerifyModels { get; set; }

    /// <summary>开发用：单独摆出快捷键总览，配合 --screenshot 检查版式。</summary>
    public static bool ShowShortcuts { get; set; }

    /// <summary>开发用：对话页换成跨天示例，检查日期分隔和完整时间。</summary>
    public static bool ChatDates { get; set; }

    /// <summary>摆出产物面板和一条逐行差异，检查文件卡与 diff 版式。</summary>
    public static bool ChatArtifacts { get; set; }

    /// <summary>把最后一条提问摆进编辑态，检查编辑框和按钮版式。</summary>
    public static bool ChatEdit { get; set; }

    /// <summary>设置页直接停在「系统设置」那一栏，省得每次手动切。</summary>
    public static bool SettingsSystem { get; set; }

    /// <summary>要打开文档预览的文件路径（配合 --page preview 截图验收）。</summary>
    public static string? PreviewPath { get; set; }

    /// <summary>开发用：截图前把主内容滚到底，检查长页面下半截。</summary>
    public static bool ScrollToEnd { get; set; }

    /// <summary>开发用：截图前把主内容滚到顶，检查消息流开头的日期分隔。</summary>
    public static bool ScrollToTop { get; set; }

    /// <summary>开发用：截图时套一个缩放值，检查 90%–125% 的版式。</summary>
    public static double? FontScale { get; set; }

    /// <summary>开发用：对话页摆一张待确认的授权卡，检查键盘提示与按钮版式。</summary>
    public static bool ApprovalDemo { get; set; }

    /// <summary>开发用：把授权卡摆成「参数已改」的状态，检查主按钮切换与还原入口。</summary>
    public static bool ApprovalEdited { get; set; }

    /// <summary>开发用：对当前焦点控件发一次 Esc，验证审批卡的键盘拒绝。</summary>
    public static bool PressEscape { get; set; }

    /// <summary>摆出「有工具在等确认」的常驻条，检查它和输入框的间距。</summary>
    public static bool ApprovalWait { get; set; }

    /// <summary>把上下文用量调到 84%，检查水位条和警示文案。</summary>
    public static bool ContextTight { get; set; }

    /// <summary>开发用：页头摆出「这一轮已经跑了一会儿」的状态，检查状态胶囊和计时。</summary>
    public static bool RunClock { get; set; }

    /// <summary>开发用：侧栏摆一条置顶任务，检查置顶分组和行首标记。</summary>
    public static bool DemoPin { get; set; }

    /// <summary>开发用：给示例会话一个「上次读到 45%」的位置，检查打开时是否接着读而不是跳到底部。</summary>
    public static double? ReadingRatio { get; set; }

    /// <summary>开发用：设置页摆出「数值填错」的样子，检查字段下面的红字和保存拦截。</summary>
    public static bool SettingsInvalid { get; set; }

    /// <summary>开发用：对话页摆一条导出回执，检查一次性提示条和「打开所在文件夹」。</summary>
    public static bool DemoToast { get; set; }

    /// <summary>开发用：输入框摆出「还没配 Key」的就地提示，检查麦克风那条说明。</summary>
    public static bool ComposerHint { get; set; }

    /// <summary>开发用：MCP 第一张卡片进入「确认删除」态，检查二次确认。</summary>
    public static bool McpDelete { get; set; }

    /// <summary>开发用：从对话进设置再返回，验收「回到原来那条任务」。</summary>
    public static bool VerifyBack { get; set; }

    /// <summary>开发用：先滚动再删任务，验收「删掉的存档不会被写回来」。</summary>
    public static bool VerifyDelete { get; set; }

    /// <summary>开发用：填好查找词后立刻关掉查找条，验收高亮被清干净。</summary>
    public static bool ChatSearchClose { get; set; }

    /// <summary>开发用：反复置顶/取消置顶一条任务，验收分组顺序和存档里的 pinned。</summary>
    public static bool VerifyPin { get; set; }

    /// <summary>开发用：先开一条任务再进设置，检查页头上那条「返回某条任务」的面包屑。</summary>
    public static bool SettingsFromChat { get; set; }

    /// <summary>开发用：MCP 删除走三步（确认态 → 真删 → 撤销），逐步比对 mcp.json 的原文。</summary>
    public static bool VerifyMcp { get; set; }

    /// <summary>开发用：把一张本地图片塞进输入框，检查压缩与角标。</summary>
    public static string? ComposerImage { get; set; }

    /// <summary>开发用：一条会话卡在审批上并切到后台，验收窗口标题仍然看得见。</summary>
    public static bool VerifyApprovalBackground { get; set; }

    /// <summary>开发用：保存一轮后检查侧栏行对象有没有被换掉（N8 的核心）。</summary>
    public static bool VerifySidebar { get; set; }

    /// <summary>摆出「重跑会删掉后面几轮」的确认弹窗，检查版式与红色主按钮。</summary>
    public static bool ShowRetryConfirm { get; set; }

    /// <summary>是否用了隔离数据目录（--data-dir）。隔离时截图可以放心读写设置，不碰真实数据。</summary>
    public static bool IsolatedData { get; set; }
}
