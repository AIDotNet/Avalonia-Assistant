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

    /// <summary>
    /// 把一整段标准 JSON 里的服务端批量写进 <c>mcp.json</c>。认三种常见写法：
    /// <c>{ "mcpServers": { ... } }</c>、VS Code 的 <c>{ "servers": { ... } }</c>（含 <c>{"mcp":{"servers":…}}</c>）、
    /// 以及直接给一层「名称 → 配置」。
    /// 每条配置原样保留（<c>env</c> / <c>headers</c> / <c>url</c> / <c>cwd</c> 都不动），只做三件事：
    /// 缺 <c>exposure</c> 时补 <c>direct</c>、npm 这类命令补 Windows 启动层、校验能起得来。
    /// </summary>
    public static McpImportResult ImportMcpServers(string configPath, string json, bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new InvalidOperationException("mcp.json 路径不可用，先选一个工作区。");
        }

        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("先把 JSON 粘贴进来，或者从文件导入。");
        }

        var imported = ReadServerEntries(json);

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
        var added = new List<string>();
        var replaced = new List<string>();
        var skipped = new List<string>();

        foreach (var (name, entry) in imported)
        {
            if (servers.ContainsKey(name) && !overwrite)
            {
                skipped.Add(name);
                continue;
            }

            if (servers.ContainsKey(name))
            {
                replaced.Add(name);
            }
            else
            {
                added.Add(name);
            }

            servers[name] = entry;
        }

        if (added.Count > 0 || replaced.Count > 0)
        {
            root["mcpServers"] = servers;

            var directory = Path.GetDirectoryName(configPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(configPath, root.ToJsonString(WriteOptions));
        }

        return new McpImportResult(added, replaced, skipped);
    }

    /// <summary>
    /// 解析粘贴进来的 JSON，挑出服务端那一层并逐条整理成可写入的配置。
    /// 结构性错误（不是对象、没有 command/url、名字不合法）直接抛，不做半截导入。
    /// </summary>
    private static List<(string Name, JsonObject Entry)> ReadServerEntries(string json)
    {
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                // 从聊天窗 / 文档里复制过来的 JSON 常带尾逗号和注释，读的时候放过它们。
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"粘贴的内容不是合法 JSON：{ex.Message}");
        }

        if (parsed is not JsonObject root)
        {
            throw new InvalidOperationException("顶层要是一个对象，例如 { \"mcpServers\": { … } }。");
        }

        var block = ExtractServerBlock(root);
        if (block.Count == 0)
        {
            throw new InvalidOperationException("这份 JSON 里没有服务端配置。");
        }

        var results = new List<(string Name, JsonObject Entry)>(block.Count);
        var badNames = new List<string>();

        foreach (var (rawName, node) in block)
        {
            var name = rawName.Trim();
            if (!IsValidServerName(name))
            {
                badNames.Add(rawName);
                continue;
            }

            if (node is not JsonObject entry)
            {
                throw new InvalidOperationException($"「{name}」的配置要是一个对象，现在是 {node?.GetValueKind().ToString() ?? "空"}。");
            }

            if (ReadString(entry["command"]) is null && ReadString(entry["url"]) is null)
            {
                throw new InvalidOperationException($"「{name}」既没有 command 也没有 url，不知道该怎么连。");
            }

            results.Add((name, PrepareEntry(entry)));
        }

        if (badNames.Count > 0)
        {
            throw new InvalidOperationException(
                $"服务端名称只能用字母、数字、下划线和短横线：{string.Join('、', badNames)}。");
        }

        return results;
    }

    /// <summary>
    /// 三种写法都认：<c>mcpServers</c>、VS Code 的 <c>servers</c>（含外面套一层 <c>mcp</c>）、
    /// 以及直接「名称 → 配置」的一层映射。都不匹配就当空。
    /// </summary>
    private static JsonObject ExtractServerBlock(JsonObject root)
    {
        if (root["mcpServers"] is JsonObject mcpServers)
        {
            return mcpServers;
        }

        if (root["servers"] is JsonObject servers)
        {
            return servers;
        }

        if (root["mcp"] is JsonObject mcp && mcp["servers"] is JsonObject nested)
        {
            return nested;
        }

        if (root.Count > 0 && root.All(pair => pair.Value is JsonObject))
        {
            return root;
        }

        return new JsonObject();
    }

    /// <summary>拷一份再动，别把调用方传进来的节点挪了位置：补 exposure、补 Windows 启动层。</summary>
    private static JsonObject PrepareEntry(JsonObject entry)
    {
        var copy = entry.DeepClone().AsObject();

        var command = ReadString(copy["command"]);
        if (command is not null && ReadArgs(copy["args"]) is { } args)
        {
            var (normalized, normalizedArgs) = NormalizeCommand(command, args);
            if (!string.Equals(normalized, command, StringComparison.Ordinal))
            {
                var array = new JsonArray();
                foreach (var arg in normalizedArgs)
                {
                    array.Add(arg);
                }

                copy["command"] = normalized;
                copy["args"] = array;
            }
        }

        // 不写 exposure 的话模型直接调不到这个服务端的工具。
        if (copy["exposure"] is null)
        {
            copy["exposure"] = "direct";
        }

        return copy;
    }

    private static string? ReadString(JsonNode? node)
        => node is JsonValue value
           && value.TryGetValue<string>(out var text)
           && !string.IsNullOrWhiteSpace(text)
            ? text.Trim()
            : null;

    /// <summary>args 是字符串数组就返回列表；没写返回空列表；写法奇怪返回 <c>null</c>（保持原样不动）。</summary>
    private static List<string>? ReadArgs(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is not JsonArray array)
        {
            return null;
        }

        var args = new List<string>(array.Count);
        foreach (var item in array)
        {
            if (item is JsonValue value && value.TryGetValue<string>(out var text) && text is not null)
            {
                args.Add(text);
            }
            else
            {
                return null;
            }
        }

        return args;
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
