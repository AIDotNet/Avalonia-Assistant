using System.Collections.ObjectModel;
using Avalonia.Media;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>资料库的排序方式：按名字排，或者把刚动过的放前面。</summary>
public enum LibrarySort
{
    Name,
    Recent,
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private const int MaxEntries = 200;

    private readonly MainWindowViewModel _host;
    private readonly LibraryStore _store;

    private List<FileItemViewModel> _entries = [];
    private List<LibraryDocument> _documents = [];

    public LibraryViewModel(MainWindowViewModel host, LibraryStore store)
    {
        _host = host;
        _store = store;
        _ = RefreshAsync();
    }

    // ── 两个视图：资料库（收进来的）和工作区（本来就有的） ─────────────────

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsWorkspaceView))]
    private bool isLibraryView = true;

    public bool IsWorkspaceView => !IsLibraryView;

    public string LibraryTabText => _documents.Count > 0 ? $"资料库 {_documents.Count}" : "资料库";

    public string WorkspaceTabText
    {
        get
        {
            var count = Folders.Count + Files.Count;
            return count > 0 ? $"工作区 {count}" : "工作区";
        }
    }

    [RelayCommand]
    private void ShowLibrary() => IsLibraryView = true;

    [RelayCommand]
    private void ShowWorkspace() => IsLibraryView = false;

    // ── 资料库 ────────────────────────────────────────────────────────

    public ObservableCollection<LibraryItemViewModel> VisibleDocuments { get; } = [];

    public bool HasVisibleDocuments => VisibleDocuments.Count > 0;

    [ObservableProperty]
    private bool hasDocuments;

    [ObservableProperty]
    private bool showLibraryEmpty;

    [ObservableProperty]
    private bool showLibraryNoMatch;

    [ObservableProperty]
    private string librarySummary = string.Empty;

    public bool HasLibrarySummary => !string.IsNullOrEmpty(LibrarySummary);

    [ObservableProperty]
    private bool isImporting;

    [ObservableProperty]
    private bool isDragging;

    [ObservableProperty]
    private string libraryNotice = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LibraryNoticeBrush))]
    private bool isLibraryNoticeError;

    public bool HasLibraryNotice => !string.IsNullOrEmpty(LibraryNotice);

    /// <summary>导入成功走绿、失败走红，一条提示两副面孔。</summary>
    public IBrush LibraryNoticeBrush => IsLibraryNoticeError
        ? new SolidColorBrush(Color.Parse("#B3261E"))
        : new SolidColorBrush(Color.Parse("#2E7D32"));

    // ── 工作区 ────────────────────────────────────────────────────────

    public ObservableCollection<FileItemViewModel> Folders { get; } = [];

    public ObservableCollection<FileItemViewModel> Files { get; } = [];

    public string WorkspacePath => _host.Settings.WorkspacePath;

    public bool HasWorkspace => !string.IsNullOrWhiteSpace(WorkspacePath) && Directory.Exists(WorkspacePath);

    public string WorkspaceName
    {
        get
        {
            if (string.IsNullOrWhiteSpace(WorkspacePath))
            {
                return "未绑定工作区";
            }

            var path = WorkspacePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(path);
            return string.IsNullOrWhiteSpace(name) ? path : name;
        }
    }

    /// <summary>标题栏里显示的路径，用户目录缩写为 ~，避免长路径挤满一行。</summary>
    public string WorkspacePathDisplay
    {
        get
        {
            var path = WorkspacePath;
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (string.IsNullOrEmpty(home) || string.IsNullOrEmpty(path))
            {
                return path;
            }

            return path.StartsWith(home, StringComparison.OrdinalIgnoreCase)
                ? "~" + path[home.Length..]
                : path;
        }
    }

    public string HeaderSubtitle => HasWorkspace
        ? $"{WorkspaceName} · {WorkspacePathDisplay}"
        : "选一个文件夹，资料库会列出它的内容";

    public bool IsNameSort => Sort == LibrarySort.Name;

    public bool IsRecentSort => Sort == LibrarySort.Recent;

    public string FolderCountText => $"共 {Folders.Count} 个";

    public string FileSummaryText
    {
        get
        {
            var total = Files.Sum(item => item.Length);
            return total > 0
                ? $"{Files.Count} 个 · 共 {FormatSize(total)}"
                : $"{Files.Count} 个";
        }
    }

    /// <summary>隐藏项按钮上的文字：不显示的时候把「藏着几个」告诉用户，免得以为文件丢了。</summary>
    public string HiddenLabel => !ShowHidden && HiddenCount > 0
        ? $"隐藏项 {HiddenCount}"
        : "隐藏项";

    [ObservableProperty]
    private int hiddenCount;

    [ObservableProperty]
    private string searchText = string.Empty;

    [ObservableProperty]
    private bool showHidden;

    [ObservableProperty]
    private LibrarySort sort = LibrarySort.Name;

    [ObservableProperty]
    private bool hasFolders;

    [ObservableProperty]
    private bool hasFiles;

    /// <summary>工作区视图里有东西可列。为 false 时整块列表让位给空态。</summary>
    [ObservableProperty]
    private bool showContent;

    [ObservableProperty]
    private bool showEmptyState;

    [ObservableProperty]
    private string emptyTitle = "正在读取…";

    [ObservableProperty]
    private string emptyDescription = string.Empty;

    [ObservableProperty]
    private bool showNoMatch;

    [ObservableProperty]
    private Geometry emptyIcon = ArcIcons.FolderOpen;

    public bool ShowEmpty => ShowEmptyState || ShowNoMatch;

    [ObservableProperty]
    private string noticeText = string.Empty;

    public bool HasNotice => !string.IsNullOrEmpty(NoticeText);

    private void RaiseWorkspaceChanged()
    {
        OnPropertyChanged(nameof(WorkspacePath));
        OnPropertyChanged(nameof(WorkspacePathDisplay));
        OnPropertyChanged(nameof(WorkspaceName));
        OnPropertyChanged(nameof(HeaderSubtitle));
        OnPropertyChanged(nameof(HasWorkspace));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        RaiseWorkspaceChanged();
        NoticeText = string.Empty;
        OnPropertyChanged(nameof(HasNotice));

        await ReloadDocumentsAsync();
        await RefreshWorkspaceAsync();
    }

    // ── 资料库：导入 / 删除 / 打开 ──────────────────────────────────────

    [RelayCommand]
    private async Task ImportFilesAsync()
    {
        if (!HasWorkspace)
        {
            ShowLibraryNotice("先选一个工作区，资料要存在它的 .library 目录里。", isError: true);
            return;
        }

        var file = await _host.Picker.PickFileAsync("选择要收录进资料库的文件");
        if (string.IsNullOrWhiteSpace(file))
        {
            return;
        }

        await ImportPathsAsync([file]);
    }

    /// <summary>拖拽和「收录」按钮都走这里。UI 只给一批路径，去重和索引由 store 负责。</summary>
    public async Task ImportPathsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0)
        {
            return;
        }

        if (!HasWorkspace)
        {
            ShowLibraryNotice("先选一个工作区，资料要存在它的 .library 目录里。", isError: true);
            return;
        }

        IsImporting = true;
        try
        {
            var result = await _store.ImportAsync(WorkspacePath, paths);

            var parts = new List<string>();
            if (result.Imported.Count > 0)
            {
                parts.Add($"收录 {result.Imported.Count} 份");
            }

            if (result.Skipped.Count > 0)
            {
                parts.Add($"同名跳过 {result.Skipped.Count} 份");
            }

            if (result.Errors.Count > 0)
            {
                parts.Add(result.Errors[0]);
            }

            ShowLibraryNotice(
                parts.Count > 0 ? string.Join(" · ", parts) : "没有可收录的文件",
                isError: result.Errors.Count > 0 && result.Imported.Count == 0);

            await ReloadDocumentsAsync();
        }
        finally
        {
            IsImporting = false;
        }
    }

    [RelayCommand]
    private void CollectFile(FileItemViewModel? item)
    {
        if (item is null || item.IsDirectory)
        {
            return;
        }

        _ = ImportPathsAsync([item.FullPath]);
    }

    private void OpenDocument(LibraryItemViewModel item)
    {
        var path = Path.Combine(LibraryStore.FilesFor(WorkspacePath), item.Document.FileName);
        Ui.OpenExternal(path);
    }

    private void RemoveDocument(LibraryItemViewModel item) => _ = RemoveDocumentAsync(item);

    private async Task RemoveDocumentAsync(LibraryItemViewModel item)
    {
        if (!await _store.RemoveAsync(WorkspacePath, item.Document.Id))
        {
            return;
        }

        ShowLibraryNotice($"已从资料库移除「{item.Name}」，原文件没动", isError: false);
        await ReloadDocumentsAsync();
    }

    [RelayCommand]
    private void OpenLibraryFolder()
    {
        if (!HasWorkspace)
        {
            return;
        }

        Directory.CreateDirectory(LibraryStore.FilesFor(WorkspacePath));
        Ui.OpenExternal(LibraryStore.RootFor(WorkspacePath));
    }

    private void ShowLibraryNotice(string text, bool isError)
    {
        LibraryNotice = text;
        IsLibraryNoticeError = isError;
        OnPropertyChanged(nameof(HasLibraryNotice));
    }

    private async Task ReloadDocumentsAsync()
    {
        _documents = (await _store.LoadAsync(WorkspacePath)).ToList();
        ApplyDocumentFilter();
    }

    private void ApplyDocumentFilter()
    {
        var query = SearchText.Trim();
        var visible = _documents
            .Where(document => LibraryStore.Matches(document, query))
            .OrderByDescending(document => document.AddedAt)
            .ToList();

        VisibleDocuments.Clear();
        foreach (var document in visible)
        {
            VisibleDocuments.Add(new LibraryItemViewModel(document, OpenDocument, RemoveDocument));
        }

        HasDocuments = _documents.Count > 0;
        ShowLibraryEmpty = _documents.Count == 0;
        ShowLibraryNoMatch = _documents.Count > 0 && visible.Count == 0;
        OnPropertyChanged(nameof(HasVisibleDocuments));

        var total = _documents.Sum(document => document.Size);
        var indexed = _documents.Count(document => document.HasText);
        LibrarySummary = _documents.Count == 0
            ? string.Empty
            : $"{_documents.Count} 份 · 共 {FormatSize(total)} · {indexed} 份可引用正文";

        OnPropertyChanged(nameof(HasLibrarySummary));
        OnPropertyChanged(nameof(LibraryTabText));
    }

    // ── 工作区：枚举 / 过滤 ────────────────────────────────────────────

    private async Task RefreshWorkspaceAsync()
    {
        _entries = [];
        var workspace = WorkspacePath;

        if (!HasWorkspace)
        {
            ApplyWorkspaceFilter();
            return;
        }

        var (items, error) = await Task.Run(() => LoadEntries(workspace));
        _entries = items;

        if (error is not null)
        {
            NoticeText = "读取失败：" + error;
            OnPropertyChanged(nameof(HasNotice));
        }

        ApplyWorkspaceFilter();
    }

    [RelayCommand]
    private void OpenWorkspace() => Ui.OpenExternal(WorkspacePath);

    [RelayCommand]
    private async Task PickWorkspaceAsync()
    {
        await _host.AddWorkspaceAsync();
        RaiseWorkspaceChanged();
        await RefreshAsync();
    }

    [RelayCommand]
    private void SetSort(string? mode)
    {
        Sort = string.Equals(mode, "recent", StringComparison.Ordinal)
            ? LibrarySort.Recent
            : LibrarySort.Name;
    }

    [RelayCommand]
    private void ToggleHidden() => ShowHidden = !ShowHidden;

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    [RelayCommand]
    private void Back() => _host.OpenHome();

    partial void OnSearchTextChanged(string value)
    {
        ApplyDocumentFilter();
        ApplyWorkspaceFilter();
    }

    partial void OnShowHiddenChanged(bool value) => ApplyWorkspaceFilter();

    partial void OnSortChanged(LibrarySort value)
    {
        OnPropertyChanged(nameof(IsNameSort));
        OnPropertyChanged(nameof(IsRecentSort));
        ApplyWorkspaceFilter();
    }

    /// <summary>
    /// 过滤和排序都在内存里做：目录顶多 200 条，重新扫盘的代价比重新排一遍大得多。
    /// </summary>
    private void ApplyWorkspaceFilter()
    {
        var query = SearchText.Trim();

        HiddenCount = _entries.Count(item => item.IsHidden);

        IEnumerable<FileItemViewModel> visible = _entries;
        if (!ShowHidden)
        {
            visible = visible.Where(item => !item.IsHidden);
        }

        if (query.Length > 0)
        {
            visible = visible.Where(item =>
                item.Name.Contains(query, StringComparison.OrdinalIgnoreCase)
                || item.TypeLabel.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        // 文件夹永远排前面：这是文件管理器最基本的心智，按时间排也不该把它打散。
        var list = (Sort == LibrarySort.Recent
                ? visible.OrderByDescending(item => item.IsDirectory)
                    .ThenByDescending(item => item.ModifiedAt)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                : visible.OrderByDescending(item => item.IsDirectory)
                    .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        Folders.Clear();
        Files.Clear();
        foreach (var item in list)
        {
            if (item.IsDirectory)
            {
                Folders.Add(item);
            }
            else
            {
                Files.Add(item);
            }
        }

        HasFolders = Folders.Count > 0;
        HasFiles = Files.Count > 0;
        ShowContent = HasFolders || HasFiles;
        ShowNoMatch = query.Length > 0 && !ShowContent;

        // 没有工作区和「文件夹是空的」是两回事，分开说，用户才知道下一步该点什么。
        ShowEmptyState = !HasWorkspace || (_entries.Count == 0 && query.Length == 0);
        if (!HasWorkspace)
        {
            EmptyIcon = ArcIcons.FolderOpen;
            EmptyTitle = "还没有工作区";
            EmptyDescription = "选一个文件夹当工作区，这里会列出它的内容，点一下就用系统程序打开。";
        }
        else if (ShowNoMatch)
        {
            EmptyIcon = ArcIcons.Search;
            EmptyTitle = "没有匹配的文件";
            EmptyDescription = $"没有名字里带「{query}」的项目。换个词，或者显示隐藏项。";
        }
        else if (_entries.Count == 0)
        {
            EmptyIcon = ArcIcons.FolderOpen;
            EmptyTitle = "这个文件夹是空的";
            EmptyDescription = "让助手在这里写点东西，或者换一个已经有内容的工作区。";
        }

        OnPropertyChanged(nameof(ShowEmpty));
        OnPropertyChanged(nameof(FolderCountText));
        OnPropertyChanged(nameof(FileSummaryText));
        OnPropertyChanged(nameof(HiddenLabel));
        OnPropertyChanged(nameof(WorkspaceTabText));
    }

    private static (List<FileItemViewModel> Items, string? Error) LoadEntries(string workspace)
    {
        var items = new List<FileItemViewModel>();

        try
        {
            var entries = new DirectoryInfo(workspace)
                .EnumerateFileSystemInfos()
                .Take(MaxEntries)
                .ToList();

            foreach (var entry in entries)
            {
                var hidden = IsHiddenEntry(entry);

                if (entry is DirectoryInfo directory)
                {
                    var (count, capped) = CountChildren(directory);
                    items.Add(new FileItemViewModel(
                        directory.Name,
                        directory.FullName,
                        isDirectory: true,
                        hidden,
                        directory.LastWriteTime,
                        0,
                        capped ? "200+ 项" : $"{count} 项"));
                }
                else if (entry is FileInfo file)
                {
                    items.Add(new FileItemViewModel(
                        file.Name,
                        file.FullName,
                        isDirectory: false,
                        hidden,
                        file.LastWriteTime,
                        file.Length,
                        FormatSize(file.Length)));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (items, ex.Message);
        }

        return (items, null);
    }

    /// <summary>子项数量只数到 200，node_modules 这种目录不该为了一个数字把整棵树走完。</summary>
    private static (int Count, bool Capped) CountChildren(DirectoryInfo directory)
    {
        try
        {
            var count = 0;
            foreach (var _ in directory.EnumerateFileSystemInfos())
            {
                count++;
                if (count >= MaxEntries)
                {
                    return (MaxEntries, true);
                }
            }

            return (count, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (0, false);
        }
    }

    private static bool IsHiddenEntry(FileSystemInfo info)
    {
        if (info.Name.StartsWith('.'))
        {
            return true;
        }

        try
        {
            return (info.Attributes & FileAttributes.Hidden) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string FormatSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double size = bytes;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return $"{size:0.##} {units[unit]}";
    }
}
