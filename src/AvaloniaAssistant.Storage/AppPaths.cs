using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AvaloniaAssistant.Storage;

public static class AppPaths
{
    private static string? _root;

    public static string Root => _root ??= Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AvaloniaAssistant");

    public static string Sessions => Path.Combine(Root, "sessions");

    /// <summary>粘贴进对话的图片。会话 JSON 里只存这里的路径。</summary>
    public static string Images => Path.Combine(Root, "images");

    public static string SettingsFile => Path.Combine(Root, "settings.json");

    public static string DefaultWorkspace => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AvaloniaAssistant",
        "Workspace");

    /// <summary>覆盖数据根目录（便携模式 / 测试隔离）。必须在 EnsureCreated 前调用。</summary>
    public static void UseRoot(string root)
    {
        if (string.IsNullOrWhiteSpace(root))
        {
            throw new ArgumentException("数据目录不能为空。", nameof(root));
        }

        _root = Path.GetFullPath(root);
    }

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Sessions);
        Directory.CreateDirectory(Images);
        Directory.CreateDirectory(DefaultWorkspace);
    }
}

public static class AssistantJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };
}
