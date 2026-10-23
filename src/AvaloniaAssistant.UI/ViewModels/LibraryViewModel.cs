using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
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

public sealed partial class FileItemViewModel
{
    public required string Name { get; init; }

    public required string FullPath { get; init; }

    public required bool IsDirectory { get; init; }

    public required bool IsHidden { get; init; }

    public required DateTime ModifiedAt { get; init; }

    /// <summary>右上角的小标签：文件是扩展名（MD / CS），文件夹是「文件夹」。</summary>
    public required string TypeLabel { get; init; }

    /// <summary>大小列：文件是体积，文件夹是子项数量。</summary>
    public required string SizeText { get; init; }

    public required long Length { get; init; }

    public required IBrush TileBackground { get; init; }

    public required IBrush IconBrush { get; init; }

    public Geometry Icon => IsDirectory ? ArcIcons.Folder : ArcIcons.FileText;

    /// <summary>近两天说「今天 / 昨天」，更早的只留月日和时分——目录列表里没人关心年份。</summary>
    public string ModifiedText
    {
        get
        {
            var today = DateTime.Now.Date;
            if (ModifiedAt.Date == today)
            {
                return "今天 " + ModifiedAt.ToString("HH:mm");
            }

            if (ModifiedAt.Date == today.AddDays(-1))
            {
                return "昨天 " + ModifiedAt.ToString("HH:mm");
            }

            return ModifiedAt.ToString("MM-dd HH:mm");
        }
    }

    [RelayCommand]
    private void Open() => Ui.OpenExternal(FullPath);
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private const int MaxEntries = 200;

    private static readonly (string Label, IBrush Background, IBrush Foreground) FolderStyle =
        ("文件夹", Tint("#FBF1DE"), Tint("#9A6516"));

    private readonly MainWindowViewModel _host;
    private List<FileItemViewModel> _entries = [];

    public LibraryViewModel(MainWindowViewModel host)
    {
        _host = host;
        _ = RefreshAsync();
    }

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

    /// <summary>有东西可列。为 false 时整块列表让位给空态。</summary>
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
    private string noticeText = string.Empty;

    public bool HasNotice => !string.IsNullOrEmpty(NoticeText);

    /// <summary>工作区变了要连带刷新标题栏，光刷列表不够。</summary>
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

        _entries = [];
        var workspace = WorkspacePath;

        if (!HasWorkspace)
        {
            ApplyFilter();
            return;
        }

        var (items, error) = await Task.Run(() => LoadEntries(workspace));
        _entries = items;

        if (error is not null)
        {
            NoticeText = "读取失败：" + error;
            OnPropertyChanged(nameof(HasNotice));
        }

        ApplyFilter();
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

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnShowHiddenChanged(bool value) => ApplyFilter();

    partial void OnSortChanged(LibrarySort value)
    {
        OnPropertyChanged(nameof(IsNameSort));
        OnPropertyChanged(nameof(IsRecentSort));
        ApplyFilter();
    }

    /// <summary>
    /// 过滤和排序都在内存里做：目录顶多 200 条，重新扫盘的代价比重新排一遍大得多。
    /// </summary>
    private void ApplyFilter()
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
            EmptyTitle = "还没有工作区";
            EmptyDescription = "选一个文件夹当工作区，这里会列出它的内容，点一下就用系统程序打开。";
        }
        else if (_entries.Count == 0)
        {
            EmptyTitle = "这个文件夹是空的";
            EmptyDescription = "让助手在这里写点东西，或者换一个已经有内容的工作区。";
        }
        else if (ShowNoMatch)
        {
            EmptyTitle = "没有匹配的文件";
            EmptyDescription = $"没有名字里带「{query}」的项目，换个词或者把隐藏项也显示出来。";
        }

        OnPropertyChanged(nameof(FolderCountText));
        OnPropertyChanged(nameof(FileSummaryText));
        OnPropertyChanged(nameof(HiddenLabel));
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
                    var size = capped ? "200+ 项" : $"{count} 项";
                    items.Add(new FileItemViewModel
                    {
                        Name = directory.Name,
                        FullPath = directory.FullName,
                        IsDirectory = true,
                        IsHidden = hidden,
                        ModifiedAt = directory.LastWriteTime,
                        TypeLabel = FolderStyle.Label,
                        SizeText = size,
                        Length = 0,
                        TileBackground = FolderStyle.Background,
                        IconBrush = FolderStyle.Foreground,
                    });
                }
                else if (entry is FileInfo file)
                {
                    var style = StyleFor(file.Extension);
                    items.Add(new FileItemViewModel
                    {
                        Name = file.Name,
                        FullPath = file.FullName,
                        IsDirectory = false,
                        IsHidden = hidden,
                        ModifiedAt = file.LastWriteTime,
                        TypeLabel = style.Label,
                        SizeText = FormatSize(file.Length),
                        Length = file.Length,
                        TileBackground = style.Background,
                        IconBrush = style.Foreground,
                    });
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

    /// <summary>
    /// 文件类型配色。底色都很浅，压在白底列表里像一张标签纸，不抢文件名的位置。
    /// </summary>
    private static (string Label, IBrush Background, IBrush Foreground) StyleFor(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cs" or "csproj" or "sln" or "slnx" or "props" or "targets" or "axaml" or "xaml"
                or "json" or "yml" or "yaml" or "toml" or "ps1" or "sh" or "bat" or "cmd"
                => (Label(ext), Tint("#EDF0FA"), Tint("#3D57A5")),
            "md" or "txt" or "rst" or "log"
                => (Label(ext), Tint("#EAF4EC"), Tint("#2E7D32")),
            "png" or "jpg" or "jpeg" or "gif" or "webp" or "svg" or "ico" or "bmp"
                => (Label(ext), Tint("#F4EEFB"), Tint("#7350B5")),
            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "csv"
                => (Label(ext), Tint("#FCEEE2"), Tint("#A9581A")),
            "mp3" or "wav" or "mp4" or "mov" or "mkv" or "flac"
                => (Label(ext), Tint("#FDEBEE"), Tint("#B03A48")),
            "zip" or "7z" or "rar" or "gz" or "tar"
                => (Label(ext), Tint("#F1F1EE"), Tint("#6F6F69")),
            "" => ("FILE", Tint("#F2F2EF"), Tint("#6F6F69")),
            _ => (Label(ext), Tint("#F2F2EF"), Tint("#6F6F69")),
        };

        static string Label(string ext)
            => ext.Length is > 0 and <= 4
                ? ext.ToUpperInvariant()
                : ext[..Math.Min(4, ext.Length)].ToUpperInvariant();
    }

    /// <summary>颜色要在线程池线程上建（枚举目录时），所以用不可变画刷，别拿 AvaloniaObject 跨线程。</summary>
    private static IBrush Tint(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

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
