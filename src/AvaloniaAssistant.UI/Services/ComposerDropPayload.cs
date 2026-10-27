using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;

namespace AvaloniaAssistant.UI.Services;

/// <summary>
/// 一次拖放的「内容快照」。拖放数据只在事件处理期间有效（OLE 那边事件返回就释放），
/// 所以先把它抄成能长期持有的东西：路径列表、文件夹个数、有没有位图。
/// 接不接、加什么，后面都只看这份快照。
/// </summary>
internal readonly record struct ComposerDropPayload(
    IReadOnlyList<string> Paths,
    int Folders,
    bool HasBitmap)
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".tif", ".tiff", ".ico", ".avif",
    };

    public bool HasPaths => Paths.Count > 0;

    /// <summary>有能接的东西。纯文字拖放时为 false，那一下要留给输入框自己处理。</summary>
    public bool Accepts => HasPaths || HasBitmap;

    /// <summary>路径里有几张图。拖进来的图该当图片发，不能当文本引用——引用会把二进制读成乱码。</summary>
    public int Images => Paths.Count(IsImagePath);

    public static bool IsImagePath(string path)
        => !string.IsNullOrWhiteSpace(path) && ImageExtensions.Contains(Path.GetExtension(path));

    /// <summary>把拖放内容抄成快照。读不出来的格式当没有，不抛给调用方。</summary>
    public static ComposerDropPayload Read(IDataTransfer data)
    {
        var paths = new List<string>();
        var folders = 0;

        try
        {
            foreach (var item in data.TryGetFiles() ?? [])
            {
                if (item is IStorageFolder)
                {
                    folders++;
                    continue;
                }

                // 云盘、虚拟文件没有本地路径：加了也读不到正文，干脆不算
                if (item.TryGetLocalPath() is { Length: > 0 } path)
                {
                    paths.Add(path);
                }
                else
                {
                    folders++;
                }
            }
        }
        catch
        {
            // 数据源给了个不认识的格式，当这次没拖文件
        }

        var hasBitmap = false;
        try
        {
            hasBitmap = data.Contains(DataFormat.Bitmap);
        }
        catch
        {
            // 同上
        }

        return new ComposerDropPayload(
            paths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            folders,
            hasBitmap);
    }

    /// <summary>
    /// 取拖进来的位图（浏览器、截图工具常走这条路，没有文件路径）。
    /// 必须在 Drop 处理里同步读完：事件一返回数据就没了。
    /// </summary>
    public static Bitmap? TryTakeBitmap(IDataTransfer data)
    {
        try
        {
            return data.TryGetBitmap();
        }
        catch
        {
            return null;
        }
    }
}
