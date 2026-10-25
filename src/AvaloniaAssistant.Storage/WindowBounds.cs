using System.Text.Json;

namespace AvaloniaAssistant.Storage;

/// <summary>
/// 窗口尺寸与最大化状态。单独存一个 window.json，而不是塞进 settings：
/// 拖动窗口会频繁触发保存，走设置那条路会连带刷新侧栏、重扫工作区。
/// </summary>
public sealed record WindowBounds(double Width, double Height, bool Maximized)
{
    private static string FilePath => System.IO.Path.Combine(AppPaths.Root, "window.json");

    public static WindowBounds? TryRead()
    {
        try
        {
            if (!System.IO.File.Exists(FilePath))
            {
                return null;
            }

            using var stream = System.IO.File.OpenRead(FilePath);
            return JsonSerializer.Deserialize<WindowBounds>(stream, AssistantJson.Options);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void Write(WindowBounds bounds)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var temp = FilePath + ".tmp";
            System.IO.File.WriteAllText(temp, JsonSerializer.Serialize(bounds, AssistantJson.Options));
            System.IO.File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记不住窗口大小不该影响退出。
        }
    }
}
