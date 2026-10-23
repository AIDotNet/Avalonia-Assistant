using Avalonia.Media;
using Avalonia.Media.Immutable;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>
/// 文件类型的标签与配色。工作区列表、资料库列表、@ 浮层三处共用，
/// 同一个扩展名在哪里都是同一个颜色，用户不用重新认一遍。
/// </summary>
internal static class FileTypeStyle
{
    public static readonly (string Label, IBrush Background, IBrush Foreground) Folder =
        ("文件夹", Tint("#FBF1DE"), Tint("#9A6516"));

    public static (string Label, IBrush Background, IBrush Foreground) For(string extension)
    {
        var ext = extension.TrimStart('.').ToLowerInvariant();
        return ext switch
        {
            "cs" or "csproj" or "sln" or "slnx" or "props" or "targets" or "axaml" or "xaml"
                or "json" or "yml" or "yaml" or "toml" or "ps1" or "sh" or "bat" or "cmd"
                => (Label(ext), Tint("#EDF0FA"), Tint("#3D57A5")),

            // 点开头的配置文件名就是全部：.gitignore 截成「GITI」比不显示还难认
            "gitignore" or "gitattributes" or "gitmodules" or "editorconfig" or "env"
                => ("CFG", Tint("#EDF0FA"), Tint("#3D57A5")),

            "md" or "txt" or "rst" or "log"
                => (Label(ext), Tint("#EAF4EC"), Tint("#2E7D32")),

            "png" or "jpg" or "jpeg" or "gif" or "webp" or "svg" or "ico" or "bmp"
                => (Label(ext), Tint("#F4EEFB"), Tint("#7350B5")),

            "pdf" or "doc" or "docx" or "xls" or "xlsx" or "ppt" or "pptx" or "csv"
                => (Label(ext), Tint("#FCEEE2"), Tint("#A9581A")),

            "mp3" or "wav" or "mp4" or "mov" or "mkv" or "flac"
                => (Label(ext), Tint("#FDEBEE"), Tint("#B03A48")),

            "" => ("FILE", Tint("#F2F2EF"), Tint("#6F6F69")),
            _ => (Label(ext), Tint("#F2F2EF"), Tint("#6F6F69")),
        };
    }

    /// <summary>颜色要在线程池线程上建（枚举目录时），所以用不可变画刷，别拿 AvaloniaObject 跨线程。</summary>
    public static IBrush Tint(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));

    private static string Label(string ext)
        => ext.Length <= 5
            ? ext.ToUpperInvariant()
            : ext[..4].ToUpperInvariant();
}

/// <summary>工作区顶层的一个文件或文件夹。</summary>
public sealed partial class FileItemViewModel
{
    public FileItemViewModel(
        string name,
        string fullPath,
        bool isDirectory,
        bool isHidden,
        DateTime modifiedAt,
        long length,
        string sizeText)
    {
        Name = name;
        FullPath = fullPath;
        IsDirectory = isDirectory;
        IsHidden = isHidden;
        ModifiedAt = modifiedAt;
        Length = length;
        SizeText = sizeText;

        var style = isDirectory ? FileTypeStyle.Folder : FileTypeStyle.For(Path.GetExtension(name));
        TypeLabel = style.Label;
        TileBackground = style.Background;
        IconBrush = style.Foreground;
    }

    public string Name { get; }

    public string FullPath { get; }

    public bool IsDirectory { get; }

    public bool IsHidden { get; }

    public DateTime ModifiedAt { get; }

    public long Length { get; }

    /// <summary>大小列：文件是体积，文件夹是子项数量。</summary>
    public string SizeText { get; }

    /// <summary>类型徽章：文件是扩展名（MD / CS），文件夹是「文件夹」。</summary>
    public string TypeLabel { get; }

    public IBrush TileBackground { get; }

    public IBrush IconBrush { get; }

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

/// <summary>
/// @ 浮层里的一个候选。可能是工作区里的文件（直接引用，不用先导入），
/// 也可能是资料库里的条目（跨会话收藏、已经建过索引）。
/// </summary>
public sealed partial class MentionItemViewModel : ObservableObject
{
    private MentionItemViewModel()
    {
    }

    public static MentionItemViewModel FromWorkspace(WorkspaceFileEntry entry)
    {
        var style = FileTypeStyle.For(System.IO.Path.GetExtension(entry.Name));
        return new MentionItemViewModel
        {
            Id = "ws:" + entry.RelativePath,
            Name = entry.Name,
            Path = entry.RelativePath,
            FullPath = entry.FullPath,
            Kind = style.Label,
            Size = entry.Size,
            IsLibrary = false,
            TileBackground = style.Background,
            IconBrush = style.Foreground,
        };
    }

    public static MentionItemViewModel FromLibrary(LibraryDocument document)
    {
        var style = FileTypeStyle.For(document.Name);
        return new MentionItemViewModel
        {
            Id = "lib:" + document.Id,
            Name = document.Name,
            Path = document.Name,
            Document = document,
            Kind = document.Kind,
            Size = document.Size,
            IsLibrary = true,
            TileBackground = style.Background,
            IconBrush = style.Foreground,
        };
    }

    public string Id { get; private init; } = string.Empty;

    public string Name { get; private init; } = string.Empty;

    /// <summary>工作区文件是相对路径，资料库条目是库里的名字。</summary>
    public string Path { get; private init; } = string.Empty;

    public string? FullPath { get; private init; }

    public LibraryDocument? Document { get; private init; }

    public string Kind { get; private init; } = "FILE";

    public long Size { get; private init; }

    public bool IsLibrary { get; private init; }

    public string SourceText => IsLibrary ? "资料库" : "工作区";

    /// <summary>副标题：工作区给完整相对路径，资料库给「资料库 · 大小」。</summary>
    public string Detail => IsLibrary
        ? $"资料库 · {FormatSize(Size)}"
        : Path;

    public IBrush TileBackground { get; private init; } = FileTypeStyle.Tint("#F2F2EF");

    public IBrush IconBrush { get; private init; } = FileTypeStyle.Tint("#6F6F69");

    public Geometry Icon => ArcIcons.FileText;

    /// <summary>键盘上下移动时的选中态。</summary>
    [ObservableProperty]
    private bool isSelected;

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

/// <summary>
/// 资料库里的一条。和工作区文件长得像但是两种东西：这个是「已经收进库、能被 @ 引用」的副本，
/// 点开看的是 <c>.library/files</c> 里那份，原文件后来被移动删除都不影响。
/// </summary>
public sealed partial class LibraryItemViewModel : ObservableObject
{
    private readonly Action<LibraryItemViewModel>? _open;
    private readonly Action<LibraryItemViewModel>? _remove;

    public LibraryItemViewModel(
        LibraryDocument document,
        Action<LibraryItemViewModel>? open = null,
        Action<LibraryItemViewModel>? remove = null)
    {
        Document = document;
        _open = open;
        _remove = remove;

        var style = FileTypeStyle.For(document.Name);
        TypeLabel = document.Kind;
        TileBackground = style.Background;
        IconBrush = style.Foreground;
    }

    public LibraryDocument Document { get; }

    public string Name => Document.Name;

    public string TypeLabel { get; }

    public string Summary => string.IsNullOrWhiteSpace(Document.Summary) ? "（无摘要）" : Document.Summary!;

    public string SizeText => FormatSize(Document.Size);

    public string AddedText => Document.AddedAt.ToLocalTime() switch
    {
        var added when added.Date == DateTimeOffset.Now.Date => "今天 " + added.ToString("HH:mm"),
        var added when added.Date == DateTimeOffset.Now.Date.AddDays(-1) => "昨天 " + added.ToString("HH:mm"),
        var added => added.ToString("MM-dd"),
    };

    /// <summary>能不能把正文交给模型；二进制文件只能按名字引用。</summary>
    public string IndexText => Document.HasText ? $"{Document.Text!.Length:N0} 字" : "无正文";

    public bool HasText => Document.HasText;

    public bool CanRemove => _remove is not null;

    /// <summary>@ 浮层里键盘上下移动时的选中态。工作区列表不用它，一直是 false。</summary>
    [ObservableProperty]
    private bool isSelected;

    public IBrush TileBackground { get; }

    public IBrush IconBrush { get; }

    public Geometry Icon => ArcIcons.FileText;

    [RelayCommand]
    private void Open() => _open?.Invoke(this);

    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);

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
