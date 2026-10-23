using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent.Tools;

/// <summary>内置工具的统一入口；Agent 层只认这个接口。</summary>
internal interface IAgentTool
{
    AgentToolInfo Info { get; }

    Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken);
}

internal sealed record ToolInvocation(object? Arguments, string? WorkspacePath, PermissionMode Permission)
{
    public JsonElement Json => JsonArgs.ToElement(Arguments);
}

internal static class JsonArgs
{
    public static JsonElement ToElement(object? args) => args switch
    {
        null => default,
        JsonElement element => element,
        JsonNode node => JsonSerializer.SerializeToElement(node),
        _ => JsonSerializer.SerializeToElement(args),
    };

    public static string? OptionalString(JsonElement json, string name)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.ToString(),
        };
    }

    public static string RequiredString(JsonElement json, string name)
    {
        var value = OptionalString(json, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"缺少参数 {name}。");
        }

        return value;
    }

    public static int OptionalInt(JsonElement json, string name, int fallback)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : fallback;
    }

    public static bool OptionalBool(JsonElement json, string name, bool fallback)
    {
        if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(name, out var value))
        {
            return fallback;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => bool.TryParse(value.ToString(), out var parsed) ? parsed : fallback,
        };
    }
}

internal static class ToolPaths
{
    public static string Resolve(ToolInvocation invocation, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("路径不能为空。");
        }

        var root = string.IsNullOrWhiteSpace(invocation.WorkspacePath)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : invocation.WorkspacePath!;

        var full = Path.GetFullPath(
            Path.IsPathRooted(path)
                ? path
                : Path.Combine(root, path));

        if (invocation.Permission != PermissionMode.FullAccess)
        {
            var rootFull = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var inside = candidate.Equals(rootFull, StringComparison.OrdinalIgnoreCase)
                || candidate.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

            if (!inside)
            {
                throw new InvalidOperationException(
                    $"路径不在工作区内：{full}\n当前权限为「{PermissionModeText.Describe(invocation.Permission)}」，需要「完全访问」才能离开工作区。");
            }
        }

        return full;
    }
}

internal static class ToolText
{
    public static string Cap(string text, int limit = 20_000)
        => text.Length <= limit ? text : text[..limit] + $"\n…（已截断，共 {text.Length} 字符）";

    public static string HumanSize(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
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
