using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AvaloniaAssistant.Storage;

/// <summary>
/// 把技能和 MCP 服务端「装」进 CodingAgent 认的目录。UI 只负责选目录 / 选 zip、填表单，
/// 校验与写入都在这里，方便直接测。
/// </summary>
public static class CapabilityInstaller
{
    private const string ManifestFileName = "SKILL.md";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
    };

    /// <summary>
    /// 把一个技能目录复制到技能根目录下。源目录必须直接包含 <c>SKILL.md</c>。
    /// 返回安装后的目录；重名或缺少 <c>SKILL.md</c> 抛 <see cref="InvalidOperationException"/>。
    /// </summary>
    public static string InstallSkill(string sourceDirectory, string skillsRoot)
    {
        if (string.IsNullOrWhiteSpace(sourceDirectory) || !Directory.Exists(sourceDirectory))
        {
            throw new InvalidOperationException("技能目录不存在。");
        }

        if (!HasManifest(sourceDirectory))
        {
            throw new InvalidOperationException("选中的文件夹里没有 SKILL.md，不是技能目录。");
        }

        var name = new DirectoryInfo(sourceDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Name;
        return InstallDirectory(sourceDirectory, SanitizeName(name), skillsRoot);
    }

    /// <summary>
    /// 从 zip 技能包装一个技能：解到临时目录，找到唯一的 <c>SKILL.md</c>，再复制进技能根目录。
    /// 包里可以有外层文件夹，也可以把 <c>SKILL.md</c> 放在压缩包根上（此时用包名当技能名）。
    /// </summary>
    public static string InstallSkillZip(string zipPath, string skillsRoot)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
        {
            throw new InvalidOperationException("技能包不存在。");
        }

        if (!zipPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("只支持 .zip 技能包，先把它压缩成 zip 再装。");
        }

        var temp = Path.Combine(Path.GetTempPath(), "avalonia-assistant-skill-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(temp);

        try
        {
            try
            {
                // .NET 的 ExtractToDirectory 会拒绝跳到目标目录外的条目（zip slip），不用自己再拼路径校验。
                ZipFile.ExtractToDirectory(zipPath, temp);
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                throw new InvalidOperationException($"技能包解不开：{ex.Message}");
            }

            var manifests = Directory
                .EnumerateFiles(temp, "*", SearchOption.AllDirectories)
                .Where(file => string.Equals(Path.GetFileName(file), ManifestFileName, StringComparison.OrdinalIgnoreCase))
                .Where(file => !IsIgnored(Path.GetRelativePath(temp, file)))
                .ToList();

            if (manifests.Count == 0)
            {
                throw new InvalidOperationException("技能包里没有 SKILL.md。");
            }

            if (manifests.Count > 1)
            {
                throw new InvalidOperationException(
                    $"一个技能包里只能装一个技能，这里找到 {manifests.Count} 个 SKILL.md。请分开压缩后再装。");
            }

            var source = Path.GetDirectoryName(manifests[0])!;
            var name = string.Equals(
                Path.TrimEndingDirectorySeparator(source),
                Path.TrimEndingDirectorySeparator(temp),
                StringComparison.OrdinalIgnoreCase)
                ? Path.GetFileNameWithoutExtension(zipPath)
                : new DirectoryInfo(source).Name;

            return InstallDirectory(source, SanitizeName(name), skillsRoot);
        }
        finally
        {
            TryDeleteDirectory(temp);
        }
    }

    /// <summary>
    /// 往 <c>mcp.json</c> 里加一个 stdio 服务端。已有文件的其他内容原样保留，
    /// <c>exposure</c> 固定写 <c>direct</c>：不写的话模型直接调不到这个服务端的工具。
    /// </summary>
    public static void AddMcpServer(string configPath, string name, string command, string arguments)
    {
        var trimmedName = (name ?? string.Empty).Trim();
        if (!IsValidServerName(trimmedName))
        {
            throw new InvalidOperationException("服务端名称只能用字母、数字、下划线和短横线。");
        }

        if (string.IsNullOrWhiteSpace(command))
        {
            throw new InvalidOperationException("启动命令不能为空，例如 npx。");
        }

        JsonObject root;
        if (File.Exists(configPath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(configPath)) as JsonObject ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException($"{Path.GetFileName(configPath)} 不是合法 JSON：{ex.Message}");
            }
        }
        else
        {
            root = new JsonObject();
        }

        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        if (servers.ContainsKey(trimmedName))
        {
            throw new InvalidOperationException($"已经有一个叫「{trimmedName}」的服务端了，换个名字再试。");
        }

        var entry = new JsonObject
        {
            ["exposure"] = "direct",
        };

        var args = (arguments ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (commandName, commandArgs) = NormalizeCommand(command.Trim(), args);
        entry["command"] = commandName;
        if (commandArgs.Count > 0)
        {
            var array = new JsonArray();
            foreach (var arg in commandArgs)
            {
                array.Add(arg);
            }

            entry["args"] = array;
        }

        servers[trimmedName] = entry;
        root["mcpServers"] = servers;

        var directory = Path.GetDirectoryName(configPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(configPath, root.ToJsonString(WriteOptions));
    }

    /// <summary>删掉 <c>mcp.json</c> 里的一个服务端；删到空时保留空对象，不删文件。</summary>
    public static bool RemoveMcpServer(string configPath, string name)
    {
        if (!File.Exists(configPath))
        {
            return false;
        }

        if (JsonNode.Parse(File.ReadAllText(configPath)) is not JsonObject root
            || root["mcpServers"] is not JsonObject servers
            || !servers.Remove(name))
        {
            return false;
        }

        File.WriteAllText(configPath, root.ToJsonString(WriteOptions));
        return true;
    }

    private static bool IsValidServerName(string name)
        => name.Length > 0
           && name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    /// <summary>npm / pnpm 这一族在 Windows 上只有 <c>.cmd</c> 垫片，直接当 FileName 起不来。</summary>
    private static readonly HashSet<string> ScriptCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "npx",
        "npm",
        "pnpm",
        "yarn",
        "bunx",
        "corepack",
    };

    /// <summary>
    /// Windows 上把 <c>npx foo</c> 包成 <c>cmd /c npx foo</c>。
    /// MCP 的 stdio 传输用 <c>UseShellExecute = false</c> 直接启动进程，而 <c>CreateProcess</c>
    /// 不认 <c>.cmd</c> / <c>.bat</c> 垫片：写 <c>"command": "npx"</c> 会静默失败（服务端永远连不上），
    /// 只有 <c>cmd /c</c> 才是可用的写法。用户在表单里按直觉填 npx 时替他把这层补上。
    /// </summary>
    private static (string Command, IReadOnlyList<string> Args) NormalizeCommand(
        string command,
        IReadOnlyList<string> args)
    {
        if (!OperatingSystem.IsWindows())
        {
            return (command, args);
        }

        var fileName = Path.GetFileName(command);
        var isScript = fileName.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
                       || fileName.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)
                       || (!Path.HasExtension(fileName) && ScriptCommands.Contains(fileName));

        if (!isScript)
        {
            return (command, args);
        }

        var wrapped = new List<string>(args.Count + 2) { "/c", command };
        wrapped.AddRange(args);
        return ("cmd", wrapped);
    }

    private static string InstallDirectory(string source, string name, string skillsRoot)
    {
        if (string.IsNullOrWhiteSpace(skillsRoot))
        {
            throw new InvalidOperationException("技能目录不可用，先选一个工作区。");
        }

        var target = Path.Combine(skillsRoot, name);
        if (Directory.Exists(target))
        {
            throw new InvalidOperationException($"技能「{name}」已经装过了。要更新请先在技能目录里删掉它。");
        }

        if (string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(source)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("技能源目录就是安装目标目录。");
        }

        Directory.CreateDirectory(skillsRoot);
        CopyDirectory(source, target);
        return target;
    }

    /// <summary>技能名会变成文件夹名：去掉路径分隔符和非法字符，避免装到技能目录外面。</summary>
    private static string SanitizeName(string raw)
    {
        var name = (raw ?? string.Empty).Trim().TrimEnd('.');
        foreach (var ch in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(ch, '-');
        }

        name = name.Trim();
        if (name.Length == 0 || name is "." or "..")
        {
            throw new InvalidOperationException("技能名不合法，请用字母、数字或中文命名。");
        }

        return name;
    }

    private static bool HasManifest(string directory)
        => Directory
            .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Any(file => string.Equals(Path.GetFileName(file), ManifestFileName, StringComparison.OrdinalIgnoreCase));

    /// <summary>跳过 macOS 压缩包里的 __MACOSX 和隐藏目录，别把它们的文件当成技能。</summary>
    private static bool IsIgnored(string relativePath)
        => relativePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.StartsWith('.') || segment.Equals("__MACOSX", StringComparison.OrdinalIgnoreCase));

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: false);
        }

        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 临时目录清不掉不影响安装结果，交给系统清理。
        }
    }
}
