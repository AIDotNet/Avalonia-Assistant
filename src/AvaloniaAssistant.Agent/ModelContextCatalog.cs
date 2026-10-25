using System.Text.Json;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 本地模型目录里的上下文窗口。
/// <para>
/// pi 的 <c>models-store.json</c> 给每个模型都记了 <c>contextWindow</c>，但那是 CLI 的目录，
/// UI 层读不到。水位条和「自动压缩」阈值以前各自写死 128k，换成 32k 或 200k 的模型就全错位；
/// 这里读一次、按文件修改时间缓存，两处拿同一个数。
/// </para>
/// <para>目录里没有的模型返回 null，由调用方给保守默认值——不假装知道。</para>
/// </summary>
internal static class ModelContextCatalog
{
    /// <summary>查不到时的保守值。和以前写死的 128k 一致，至少不比原来更差。</summary>
    public const long FallbackWindow = 128_000;

    private static readonly object Gate = new();
    private static Dictionary<string, long>? _windows;
    private static DateTime _stamp = DateTime.MinValue;

    public static long? Find(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return null;
        }

        var windows = Snapshot();
        return windows.TryGetValue(modelId.Trim(), out var value) ? value : null;
    }

    /// <summary>模型在目录里的窗口；查不到就给保守默认值。</summary>
    public static long Resolve(string? modelId) => Find(modelId) ?? FallbackWindow;

    /// <summary>目录里有没有这个模型。界面拿它决定要不要标「估算」。</summary>
    public static bool IsKnown(string? modelId) => Find(modelId) is not null;

    private static Dictionary<string, long> Snapshot()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".pi",
            "agent",
            "models-store.json");

        DateTime stamp;
        try
        {
            stamp = File.Exists(path) ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        }
        catch (IOException)
        {
            stamp = DateTime.MinValue;
        }

        lock (Gate)
        {
            if (_windows is not null && stamp == _stamp)
            {
                return _windows;
            }

            _windows = Load(path);
            _stamp = stamp;
            return _windows;
        }
    }

    /// <summary>解析一个 models-store.json。internal 是给测试用的：真实路径在用户目录里，不该让测试去碰。</summary>
    internal static Dictionary<string, long> Load(string path)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (!File.Exists(path))
        {
            return result;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return result;
            }

            // 常见形状是「provider → { models: [...] }」；外面再套一层 providers 也认
            var providers = document.RootElement.TryGetProperty("providers", out var nested)
                            && nested.ValueKind == JsonValueKind.Object
                ? nested
                : document.RootElement;

            foreach (var provider in providers.EnumerateObject())
            {
                if (provider.Value.ValueKind != JsonValueKind.Object
                    || !provider.Value.TryGetProperty("models", out var models)
                    || models.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var model in models.EnumerateArray())
                {
                    if (model.ValueKind != JsonValueKind.Object
                        || !model.TryGetProperty("id", out var id)
                        || id.ValueKind != JsonValueKind.String
                        || !model.TryGetProperty("contextWindow", out var window)
                        || window.ValueKind != JsonValueKind.Number
                        || !window.TryGetInt64(out var value)
                        || value <= 0)
                    {
                        continue;
                    }

                    var key = id.GetString();
                    if (!string.IsNullOrWhiteSpace(key))
                    {
                        result.TryAdd(key, value);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // 目录坏了不该影响开新会话：当作查不到，走保守值
        }

        return result;
    }
}
