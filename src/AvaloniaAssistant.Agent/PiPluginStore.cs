using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AvaloniaAssistant.Abstractions;
using Pi.CodingAgent;
using Pi.CodingAgent.Core;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// pi 包的安装与发现。真正干活的是 Pi.CodingAgent 自己的 <see cref="DefaultPackageManager"/>：
/// 它把源写进 <c>settings.json</c> 的 <c>packages</c>，装到 <c>~/.pi/agent/npm</c>，
/// 下一次 <see cref="DefaultResourceLoader"/> 解析时按同一套规则把扩展/技能/提示词/主题收进来。
/// 这里只负责：把 UI 要看的清单翻译成中文能读的形状、调安装/卸载、搜 npm。
/// </summary>
public sealed class PiPluginStore : IPluginStore
{
    private const string NpmSearchEndpoint = "https://registry.npmjs.org/-/v1/search";
    private const string PiPackageKeyword = "pi-package";

    /// <summary>一次最多取多少条市场结果。npm 单页上限 250；取 100 是为了关键词过滤后还剩下东西。</summary>
    private const int MarketPageSize = 100;

    /// <summary>没有关键词时只取一屏「热门」，够填满首屏就行。</summary>
    private const int DefaultPageSize = 40;

    private readonly ISettingsProvider _settings;
    private readonly HttpClient _http;

    public PiPluginStore(ISettingsProvider settings)
    {
        _settings = settings;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("AvaloniaAssistant/1.0 (pi plugin market)");
    }

    public string PackagesRoot => Path.GetFullPath(Path.Combine(Config.GetAgentDir(), "npm"));

    public string ExtensionsRoot => Path.GetFullPath(Path.Combine(Config.GetAgentDir(), "extensions"));

    public IReadOnlyList<AgentPluginInfo> ListInstalled()
    {
        try
        {
            var manager = CreateManager(out _);
            var results = new List<AgentPluginInfo>();
            foreach (var package in manager.ListConfiguredPackages())
            {
                var entries = package.InstalledPath is { Length: > 0 } path
                    ? PluginDiscovery.FindExtensions(path)
                    : [];

                results.Add(new AgentPluginInfo(
                    package.Source,
                    PluginDiscovery.DisplayName(package.Source),
                    package.Scope,
                    package.Filtered,
                    package.InstalledPath,
                    entries));
            }

            return results;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 设置文件坏了不该让整页崩掉：空清单 + 页面自己提示。
            return [];
        }
    }

    public IReadOnlyList<AgentExtensionInfo> ListManualExtensions()
    {
        var results = new List<AgentExtensionInfo>();
        var cwd = ResolveWorkspace();
        Collect(Path.Combine(Config.GetAgentDir(), "extensions"));
        Collect(Path.Combine(cwd, Config.ConfigDirName, "extensions"));
        return results;

        void Collect(string root)
        {
            if (!Directory.Exists(root))
            {
                return;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(root))
                {
                    if (PluginDiscovery.IsExtensionFile(file))
                    {
                        results.Add(new AgentExtensionInfo(
                            Path.GetFileNameWithoutExtension(file),
                            file,
                            PluginDiscovery.IsSourceExtension(file)));
                    }
                }

                // 目录形态的扩展：入口是 index.ts / index.js / index.dll
                foreach (var dir in Directory.EnumerateDirectories(root))
                {
                    var entry = PluginDiscovery.ResolveDirectoryEntry(dir);
                    if (entry is not null)
                    {
                        results.Add(new AgentExtensionInfo(
                            Path.GetFileName(dir),
                            entry,
                            PluginDiscovery.IsSourceExtension(entry)));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 目录读不了就当没有，不拦页面。
            }
        }
    }

    public async Task<AgentPluginInstallResult> InstallAsync(
        string source,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var normalized = PluginDiscovery.NormalizeSource(source);
        if (normalized.Length == 0)
        {
            return AgentPluginInstallResult.Failed(source, "填一个包名、git 地址或本地路径。");
        }

        try
        {
            var manager = CreateManager(out var settingsManager);
            if (progress is not null)
            {
                manager.SetProgressCallback(evt =>
                {
                    if (DescribeProgress(evt) is { Length: > 0 } text)
                    {
                        progress.Report(text);
                    }
                });
            }

            var path = await manager
                .InstallAndPersistAsync(normalized, local: false, cancellationToken)
                .ConfigureAwait(false);

            // SettingsManager 的落盘是排队异步写的：不等它冲完，界面紧接着重新扫描会读到旧文件，
            // 表现就是「装完列表里没有」。
            await settingsManager.FlushAsync().ConfigureAwait(false);

            return new AgentPluginInstallResult(true, normalized, "已安装并写入 settings.json。", path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AgentPluginInstallResult.Failed(normalized, FirstLine(ex.Message));
        }
    }

    public async Task<AgentPluginInstallResult> RemoveAsync(
        string source,
        bool projectScope = false,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var manager = CreateManager(out var settingsManager);
            var removed = await manager
                .RemoveAndPersistAsync(source, local: projectScope, cancellationToken)
                .ConfigureAwait(false);

            await settingsManager.FlushAsync().ConfigureAwait(false);

            return removed
                ? new AgentPluginInstallResult(true, source, "已从 settings.json 移除并卸载。")
                : AgentPluginInstallResult.Failed(source, "settings.json 里没有这条包记录。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return AgentPluginInstallResult.Failed(source, FirstLine(ex.Message));
        }
    }

    public async Task<IReadOnlyList<AgentPluginMarketItem>> SearchAsync(
        string? query,
        CancellationToken cancellationToken = default)
    {
        var tokens = (query ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var text = "keywords:" + PiPackageKeyword;
        if (tokens.Length > 0)
        {
            text += " " + string.Join(' ', tokens);
        }

        // 有关键词时多取一些再筛：npm 的 text 搜索把额外词当「可有可无」，
        // 只搜到 40 条的话，筛完常常一条不剩，看起来像「市场里没有」。
        var size = tokens.Length > 0 ? MarketPageSize : DefaultPageSize;
        var url = $"{NpmSearchEndpoint}?text={Uri.EscapeDataString(text)}&size={size}";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var results = new List<AgentPluginMarketItem>();
        if (root?["objects"] is not JsonArray objects)
        {
            return results;
        }

        foreach (var entry in objects)
        {
            if (entry?["package"] is not JsonObject package)
            {
                continue;
            }

            var keywords = ReadStrings(package["keywords"]);
            // npm 的 text 搜索会模糊命中，必须再用关键词确认一次：
            // 否则搜「memory」会混进一堆没打 pi-package 标的普通包。
            if (!keywords.Any(k => string.Equals(k, PiPackageKeyword, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var name = ReadString(package["name"]);
            if (name.Length == 0)
            {
                continue;
            }

            var item = new AgentPluginMarketItem(
                name,
                ReadString(package["version"]),
                ReadString(package["description"]),
                ReadString(package["publisher"]?["username"]),
                ReadString(package["date"]),
                keywords,
                ReadString(package["links"]?["npm"]) is { Length: > 0 } npmUrl
                    ? npmUrl
                    : $"https://www.npmjs.com/package/{name}");

            if (tokens.Length == 0 || tokens.All(token => Matches(item, token)))
            {
                results.Add(item);
            }
        }

        return results;
    }

    /// <summary>关键词要真的出现在包名 / 描述 / 关键词里才算命中，不能把 npm 的模糊结果当命中。</summary>
    private static bool Matches(AgentPluginMarketItem item, string token)
        => item.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
           || item.Description.Contains(token, StringComparison.OrdinalIgnoreCase)
           || item.Keywords.Any(keyword => keyword.Contains(token, StringComparison.OrdinalIgnoreCase));

    private DefaultPackageManager CreateManager(out SettingsManager settingsManager)
    {
        var cwd = ResolveWorkspace();
        var agentDir = Config.GetAgentDir();
        settingsManager = SettingsManager.Create(cwd, agentDir, new SettingsManagerCreateOptions
        {
            // 项目级包只在受信任工作区里装；用户级包不受影响。
            ProjectTrusted = _settings.Current.TrustWorkspace,
        });

        return new DefaultPackageManager(settingsManager, cwd, agentDir);
    }

    private string ResolveWorkspace() => PlainWorkspace.Resolve(_settings.Current.WorkspacePath);

    private static string? DescribeProgress(PackageProgressEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.Message))
        {
            return evt.Message;
        }

        return evt.Action switch
        {
            "start" => $"正在{ActionText(evt.Type)} {evt.Source}…",
            "done" or "complete" => $"{ActionText(evt.Type)}完成：{evt.Source}",
            // npm / git 的中间事件没有可读文案，别在卡片上堆一串「处理 xxx」
            _ => null,
        };

        static string ActionText(string type) => type switch
        {
            "install" => "安装",
            "remove" => "卸载",
            "update" => "更新",
            _ => "处理",
        };
    }

    private static string ReadString(JsonNode? node)
    {
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text) && text is not null)
            {
                return text;
            }

            if (value.TryGetValue<DateTime>(out var date))
            {
                return date.ToString("yyyy-MM-dd");
            }
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ReadStrings(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return [];
        }

        var results = new List<string>(array.Count);
        foreach (var item in array)
        {
            var text = ReadString(item);
            if (text.Length > 0)
            {
                results.Add(text);
            }
        }

        return results;
    }

    /// <summary>异常消息常常是多行 npm 输出；页面一行放不下，取第一行有内容的。</summary>
    private static string FirstLine(string message)
    {
        foreach (var line in message.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
            }
        }

        return message;
    }
}

/// <summary>
/// 包清单与扩展入口的纯逻辑：不碰网络、不碰进程，方便单测。
/// </summary>
internal static class PluginDiscovery
{
    private static readonly string[] SourceExtensions = [".ts", ".tsx", ".js", ".mjs", ".cjs", ".jsx"];

    /// <summary>目录形态扩展的入口，顺序遵循 pi 上游：源码优先，编译产物兜底。</summary>
    private static readonly string[] DirectoryEntryNames = ["index.ts", "index.js", "index.dll"];

    public static bool IsSourceExtension(string path)
        => SourceExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsExtensionFile(string path)
    {
        var extension = Path.GetExtension(path);
        return IsSourceExtension(path) || extension.Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    public static string? ResolveDirectoryEntry(string directory)
    {
        foreach (var name in DirectoryEntryNames)
        {
            var candidate = Path.Combine(directory, name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// 把用户输入补成 pi 认的源：裸包名补 <c>npm:</c>；<c>@scope/name</c>、git 地址、
    /// 本地路径原样保留（它们各自有明确形态，套上 npm: 反而解析错）。
    /// </summary>
    public static string NormalizeSource(string? source)
    {
        var text = source?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return string.Empty;
        }

        // 显式协议：npm: / git: / https:// / ssh://
        if (text.Contains(':') && !LooksLikeWindowsPath(text))
        {
            return text;
        }

        // 本地路径：盘符、./ ../ ~/，或者磁盘上真有的东西。注意不能拿 pi 的 IsLocalPath 判断——
        // 它对裸包名也返回 true（「不是已知协议就算本地」），裸名会被当路径解析而装不上。
        if (LooksLikeLocalPath(text))
        {
            return text;
        }

        // github.com/owner/repo 这类没有协议的 git 源：首段是域名形态，交给 pi 的 git 解析器。
        if (LooksLikeGitHost(text))
        {
            return text;
        }

        return "npm:" + text;
    }

    private static bool LooksLikeLocalPath(string text)
        => LooksLikeWindowsPath(text)
           || text.StartsWith("./", StringComparison.Ordinal)
           || text.StartsWith("../", StringComparison.Ordinal)
           || text.StartsWith(".\\", StringComparison.Ordinal)
           || text.StartsWith("..\\", StringComparison.Ordinal)
           || text.StartsWith('/')
           || text.StartsWith('~')
           || text.Contains('\\')
           || Directory.Exists(text)
           || File.Exists(text);

    private static bool LooksLikeGitHost(string text)
    {
        var slash = text.IndexOf('/');
        if (slash <= 0)
        {
            return false;
        }

        var host = text[..slash];
        return host.Contains('.') && !host.Contains('@');
    }

    /// <summary>设置里的源写给人看时去掉 <c>npm:</c> 前缀，但保留作用域名。</summary>
    public static string DisplayName(string source)
    {
        var text = source.Trim();
        if (text.StartsWith("npm:", StringComparison.OrdinalIgnoreCase))
        {
            text = text[4..];
            // 去掉版本尾巴：@scope/name@1.2.3 只留 @scope/name
            var at = text.LastIndexOf('@');
            if (at > 0)
            {
                text = text[..at];
            }

            return text;
        }

        if (text.StartsWith("git:", StringComparison.OrdinalIgnoreCase))
        {
            return text[4..];
        }

        return Path.GetFileName(text.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : text;
    }

    /// <summary>
    /// 一个包会贡献哪些扩展入口。规则与 pi 的包发现一致：
    /// <c>package.json</c> 的 <c>pi.extensions</c> 优先，否则看 <c>extensions/</c>，最后看根目录 <c>index.*</c>。
    /// </summary>
    public static IReadOnlyList<string> FindExtensions(string packageRoot)
    {
        if (!Directory.Exists(packageRoot))
        {
            return [];
        }

        try
        {
            var manifest = ReadManifestExtensions(packageRoot);
            if (manifest.Count > 0)
            {
                return Expand(packageRoot, manifest);
            }

            var conventional = Path.Combine(packageRoot, "extensions");
            if (Directory.Exists(conventional))
            {
                return ScanDirectory(packageRoot, conventional);
            }

            var rootEntry = ResolveDirectoryEntry(packageRoot);
            return rootEntry is null ? [] : [Relative(packageRoot, rootEntry)];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string> ReadManifestExtensions(string packageRoot)
    {
        var manifestPath = Path.Combine(packageRoot, "package.json");
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        var root = JsonNode.Parse(File.ReadAllText(manifestPath));
        return root?["pi"]?["extensions"] is JsonArray array
            ? array.Select(node => node?.GetValue<string>() ?? string.Empty).Where(text => text.Length > 0).ToList()
            : [];
    }

    private static IReadOnlyList<string> Expand(string packageRoot, IReadOnlyList<string> patterns)
    {
        var results = new List<string>();
        foreach (var pattern in patterns)
        {
            var normalized = pattern.Replace('\\', '/').TrimStart('.', '/');
            if (!HasGlob(normalized))
            {
                var path = Path.Combine(packageRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(path))
                {
                    results.Add(normalized);
                }
                else if (Directory.Exists(path))
                {
                    results.AddRange(ScanDirectory(packageRoot, path));
                }

                continue;
            }

            var regex = GlobToRegex(normalized);
            foreach (var file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories))
            {
                var relative = Relative(packageRoot, file);
                if (regex.IsMatch(relative))
                {
                    results.Add(relative);
                }
            }
        }

        return results.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IReadOnlyList<string> ScanDirectory(string packageRoot, string directory)
    {
        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (IsExtensionFile(file))
            {
                results.Add(Relative(packageRoot, file));
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            if (ResolveDirectoryEntry(child) is { } entry)
            {
                results.Add(Relative(packageRoot, entry));
            }
        }

        return results;
    }

    private static string Relative(string root, string path)
        => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static bool HasGlob(string value) => value.Contains('*') || value.Contains('?');

    private static Regex GlobToRegex(string pattern)
    {
        var builder = new System.Text.StringBuilder("^");
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = pattern[index];
            if (current == '*')
            {
                if (index + 1 < pattern.Length && pattern[index + 1] == '*')
                {
                    index++;
                    if (index + 1 < pattern.Length && pattern[index + 1] == '/')
                    {
                        index++;
                    }

                    builder.Append("(?:.*/)?");
                }
                else
                {
                    builder.Append("[^/]*");
                }

                continue;
            }

            if (current == '?')
            {
                builder.Append("[^/]");
                continue;
            }

            builder.Append(Regex.Escape(current.ToString()));
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static bool LooksLikeWindowsPath(string value)
        => value.Length >= 2 && char.IsLetter(value[0]) && value[1] == ':';
}
