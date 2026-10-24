using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TKCode.Arc;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>输入框里待发送的一张图片：缩略图 + 尺寸，点右上角可以撤掉。</summary>
public sealed partial class PendingImageViewModel : ObservableObject
{
    private readonly Action<PendingImageViewModel>? _remove;

    public PendingImageViewModel(ChatImage image, Bitmap thumbnail, Action<PendingImageViewModel>? remove = null)
    {
        Image = image;
        Thumbnail = thumbnail;
        _remove = remove;
    }

    public ChatImage Image { get; }

    public Bitmap Thumbnail { get; }

    /// <summary>悬停提示：尺寸、占用、估算 token，压缩过的还写出原图尺寸。</summary>
    public string Caption => Image.DetailCaption;

    [RelayCommand]
    private void Remove() => _remove?.Invoke(this);
}

/// <summary>
/// 文件类型的标签与配色。@ 浮层和引用标签共用一套，
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

/// <summary>
/// @ 浮层里的一个候选：工作区里的一个文件，按相对路径引用。
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
            TileBackground = style.Background,
            IconBrush = style.Foreground,
        };
    }

    /// <summary>
    /// 从文件选择器拿到的任意一个文件。走和 @ 引用同一条链路，
    /// 发送时才读正文，和「把路径写进正文让模型自己去读」是两回事。
    /// </summary>
    public static MentionItemViewModel FromFile(string fullPath)
    {
        var name = System.IO.Path.GetFileName(fullPath);
        var style = FileTypeStyle.For(System.IO.Path.GetExtension(name));
        var size = 0L;
        try
        {
            size = new FileInfo(fullPath).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 读不到大小不影响引用本身，发送时还会再检查一次存在性
        }

        var item = new MentionItemViewModel
        {
            Id = "file:" + fullPath,
            Name = name,
            Path = fullPath,
            FullPath = fullPath,
            Kind = style.Label,
            Size = size,
            TileBackground = style.Background,
            IconBrush = style.Foreground,
        };

        item.RefreshExists();
        return item;
    }

    public string Id { get; private init; } = string.Empty;

    public string Name { get; private init; } = string.Empty;

    /// <summary>相对工作区的路径，重名文件靠它区分。</summary>
    public string Path { get; private init; } = string.Empty;

    public string? FullPath { get; private init; }

    public string Kind { get; private init; } = "FILE";

    public long Size { get; private init; }

    public string SizeText => FormatSize(Size);

    /// <summary>副标题：完整相对路径，重名文件靠它区分。</summary>
    public string Detail => Path;

    /// <summary>
    /// 引用的是路径，不是内容快照。文件随时可能被挪走或删掉，
    /// 以前要等发出去、模型回一句「没有这个文件」才知道。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DetailOrMissing))]
    private bool isMissing;

    public string DetailOrMissing => IsMissing ? "文件已不存在，可能被移动或删除" : Detail;

    /// <summary>发送前对一次盘。没有绝对路径的项（理论上不存在）按「在」处理。</summary>
    public bool RefreshExists()
    {
        if (string.IsNullOrWhiteSpace(FullPath))
        {
            IsMissing = false;
            return true;
        }

        IsMissing = !File.Exists(FullPath);
        return !IsMissing;
    }

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
