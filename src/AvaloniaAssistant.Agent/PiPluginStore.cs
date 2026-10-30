using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AvaloniaAssistant.Abstractions;
using Pi.CodingAgent;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;

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

    /// <summary>
    /// 插件工具清单缓存。加载一次扩展要 3~7 秒（Jint 转译 TypeScript），
    /// 每次打开技能页都重来一遍太贵；装 / 卸 / 启停 / 更新时由本类主动清掉。
    /// </summary>
    private readonly Lock _toolsLock = new();
    private (string Key, AgentPluginTools Result)? _toolsCache;

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
            var manager = CreateManager(out var settingsManager);
            var states = ReadPackageStates(settingsManager);
            var results = new List<AgentPluginInfo>();
            foreach (var package in manager.ListConfiguredPackages())
            {
                try
                {
                    var entries = package.InstalledPath is { Length: > 0 } path
                        ? PluginDiscovery.FindExtensions(path)
                        : [];

                    var state = states.GetValueOrDefault(package.Source);
                    results.Add(new AgentPluginInfo(
                        package.Source,
                        PluginDiscovery.DisplayName(package.Source),
                        package.Scope,
                        package.Filtered,
                        package.InstalledPath,
                        entries,
                        state?.IsEnabled ?? true,
                        package.InstalledPath is { Length: > 0 } root ? ReadPackageVersion(root) : null));
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
                {
                    // 单条配置坏了（手改出来的空字符串、坏路径）不能让整页清单消失，
                    // 仍然把它列出来，只是没有安装路径。
                    results.Add(new AgentPluginInfo(
                        package.Source,
                        PluginDiscovery.DisplayName(package.Source),
                        package.Scope,
                        package.Filtered,
                        package.InstalledPath,
                        []));
                }
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

    public async Task<AgentPluginTools> ListToolsAsync(CancellationToken cancellationToken = default)
    {
        var cwd = ResolveWorkspace();
        var agentDir = Config.GetAgentDir();
        var settingsManager = SettingsManager.Create(cwd, agentDir, new SettingsManagerCreateOptions
        {
            ProjectTrusted = _settings.Current.TrustWorkspace,
        });

        // 一个包都没装：不用起 JS 宿主，直接空清单（绝大多数用户是这种情况）。
        var hasPackages = settingsManager.GetPackages().Count > 0
                          || (settingsManager.GetProjectSettings().Packages?.Count ?? 0) > 0;
        if (!hasPackages)
        {
            return AgentPluginTools.Empty;
        }

        var key = ToolsCacheKey(agentDir, cwd);
        lock (_toolsLock)
        {
            if (_toolsCache is { } cached && cached.Key == key)
            {
                return cached.Result;
            }
        }

        // 这里真的把扩展加载一遍（.ts 走进程内转译），代价是几秒；
        // 好处是清单和会话里能看到的工具同源，不会出现「页面上有、模型看不到」。
        var loader = new DefaultResourceLoader(
            cwd,
            agentDir,
            settingsManager,
            extensionFactories: BuiltInExtensions.All);
        await loader.ReloadAsync(null, cancellationToken).ConfigureAwait(false);

        var results = new List<AgentPluginToolInfo>();
        foreach (var extension in loader.GetExtensions().Extensions)
        {
            if (extension.SourceInfo.Path.StartsWith(SourceInfos.BuiltinPathPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var plugin = PluginLabel(extension.SourceInfo);
            foreach (var (name, registered) in extension.Tools)
            {
                var exposure = registered.Definition.Exposure ?? ToolExposure.Default;
                results.Add(new AgentPluginToolInfo(
                    name,
                    plugin,
                    registered.Definition.Description ?? string.Empty,
                    ExposureText(exposure),
                    exposure is ToolExposure.Codemode or ToolExposure.Deferred));
            }
        }

        var ordered = results
            .OrderBy(item => item.Plugin, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Name, StringComparer.Ordinal)
            .ToList();

        // 加载失败的扩展不能吞掉：技能页要能说清「为什么一个工具都没有」。
        // 内置扩展（builtin:）的失败与应用无关，过滤掉。
        var issues = loader.GetExtensions().Errors
            .Where(error => !error.Path.StartsWith(SourceInfos.BuiltinPathPrefix, StringComparison.Ordinal))
            .Select(error => new AgentPluginLoadIssue(PluginLabelForPath(error.Path), ShortLoadError(error.Error)))
            .ToList();

        var result = new AgentPluginTools(ordered, issues);
        lock (_toolsLock)
        {
            _toolsCache = (key, result);
        }

        return result;
    }

    /// <summary>扩展文件 → 包名。与 <see cref="PluginLabel(SourceInfo)"/> 同一套规则，这里只有路径。</summary>
    private static string PluginLabelForPath(string path)
    {
        var normalized = path.Replace('\\', '/');
        const string marker = "/node_modules/";
        var index = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index >= 0)
        {
            var tail = normalized[(index + marker.Length)..];
            var slash = tail.IndexOf('/');
            return slash > 0 ? tail[..slash] : tail;
        }

        return normalized.Split('/').Last();
    }

    /// <summary>宿主会在错误后面附一大段说明，界面上只留第一行。</summary>
    private static string ShortLoadError(string error) => FirstLine(error);

    /// <summary>settings 文件一改（含手工编辑）缓存就失效；装 / 卸 / 启停也会主动清。</summary>
    private static string ToolsCacheKey(string agentDir, string cwd)
    {
        var global = Path.Combine(agentDir, "settings.json");
        var project = Path.Combine(cwd, Config.ConfigDirName, "settings.json");
        return string.Join(
            '|',
            agentDir,
            cwd,
            File.Exists(global) ? File.GetLastWriteTimeUtc(global).Ticks : 0,
            File.Exists(project) ? File.GetLastWriteTimeUtc(project).Ticks : 0);
    }

    private void InvalidateToolsCache()
    {
        lock (_toolsLock)
        {
            _toolsCache = null;
        }
    }

    public async Task<AgentPluginInstallResult> InstallAsync(
        string source,
        bool projectScope = false,
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
                .InstallAndPersistAsync(normalized, local: projectScope, cancellationToken)
                .ConfigureAwait(false);

            // SettingsManager 的落盘是排队异步写的：不等它冲完，界面紧接着重新扫描会读到旧文件，
            // 表现就是「装完列表里没有」。
            await settingsManager.FlushAsync().ConfigureAwait(false);
            InvalidateToolsCache();

            return new AgentPluginInstallResult(true, normalized, "已安装并写入 settings.json。", path);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsMissingNpm(ex))
        {
            return AgentPluginInstallResult.Failed(
                normalized,
                "找不到 npm。插件安装要用本机的 npm：先装 Node.js（含 npm），确认命令行里 npm -v 能跑。");
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
            InvalidateToolsCache();

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

    public async Task<AgentPluginInstallResult> SetEnabledAsync(
        string source,
        bool projectScope,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        try
        {
            CreateManager(out var settingsManager);
            var nodes = (projectScope
                    ? settingsManager.GetProjectSettings().Packages
                    : settingsManager.GetPackages())
                ?.ToList() ?? [];

            var index = nodes.FindIndex(node => PluginDiscovery.PackageSourceOf(node) is { } text
                                                && string.Equals(text, source, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return AgentPluginInstallResult.Failed(source, "settings.json 里没有这条包记录。");
            }

            nodes[index] = enabled ? JsonValue.Create(source) : DisabledEntry(source);
            if (projectScope)
            {
                settingsManager.SetProjectPackages(nodes);
            }
            else
            {
                settingsManager.SetPackages(nodes);
            }

            await settingsManager.FlushAsync().ConfigureAwait(false);
            InvalidateToolsCache();
            return new AgentPluginInstallResult(
                true,
                source,
                enabled ? "已启用，下一个会话生效。" : "已停用，包还留在清单里。");
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

    public async Task<IReadOnlyList<AgentPluginUpdate>> CheckUpdatesAsync(CancellationToken cancellationToken = default)
    {
        var manager = CreateManager(out _);
        var updates = await manager.CheckForAvailableUpdatesAsync(cancellationToken).ConfigureAwait(false);
        return updates
            .Select(update => new AgentPluginUpdate(update.Source, update.DisplayName, update.Type, update.Scope))
            .ToList();
    }

    public async Task<AgentPluginInstallResult> UpdateAsync(
        string? source = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
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

            await manager.UpdateAsync(source, cancellationToken).ConfigureAwait(false);
            await settingsManager.FlushAsync().ConfigureAwait(false);
            InvalidateToolsCache();
            return new AgentPluginInstallResult(true, source ?? "全部", "已更新。");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsMissingNpm(ex))
        {
            return AgentPluginInstallResult.Failed(
                source ?? "全部",
                "找不到 npm。更新要用本机的 npm：先装 Node.js（含 npm），确认命令行里 npm -v 能跑。");
        }
        catch (Exception ex)
        {
            return AgentPluginInstallResult.Failed(source ?? "全部", FirstLine(ex.Message));
        }
    }

    public async Task<AgentPluginSearchPage> SearchAsync(
        string? query,
        int from = 0,
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
        var offset = Math.Max(0, from);
        var url = $"{NpmSearchEndpoint}?text={Uri.EscapeDataString(text)}&size={size}&from={offset}";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        var results = new List<AgentPluginMarketItem>();
        if (root?["objects"] is not JsonArray objects)
        {
            return AgentPluginSearchPage.Empty;
        }

        var rawCount = objects.Count;

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

        // npm 的 total 是模糊命中的总数（动辄上万条），不能拿来当「还剩多少」。
        // 用「这一页取满没有」判断还有没有下一页，界面只需要这一个布尔。
        var hasMore = rawCount >= size;
        return new AgentPluginSearchPage(results, offset + results.Count + (hasMore ? 1 : 0));
    }

    /// <summary>关键词要真的出现在包名 / 描述 / 关键词里才算命中，不能把 npm 的模糊结果当命中。</summary>
    private static bool Matches(AgentPluginMarketItem item, string token)
        => item.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
           || item.Description.Contains(token, StringComparison.OrdinalIgnoreCase)
           || item.Keywords.Any(keyword => keyword.Contains(token, StringComparison.OrdinalIgnoreCase));

    private sealed record PackageState(bool IsEnabled);

    /// <summary>把 settings.json 里每条包的启用状态读出来，键是原始源字符串。</summary>
    private static Dictionary<string, PackageState> ReadPackageStates(SettingsManager settingsManager)
    {
        var results = new Dictionary<string, PackageState>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in settingsManager.GetPackages())
        {
            Add(node);
        }

        foreach (var node in settingsManager.GetProjectSettings().Packages ?? [])
        {
            Add(node);
        }

        return results;

        void Add(JsonNode? node)
        {
            if (PluginDiscovery.PackageSourceOf(node) is not { Length: > 0 } source)
            {
                return;
            }

            results[source] = new PackageState(!PluginDiscovery.IsDisabledEntry(node));
        }
    }

    /// <summary>停用用的对象形态：pi 把空数组理解成「这一类全不要」，包本身还留在清单里。</summary>
    private static JsonObject DisabledEntry(string source) => PluginDiscovery.DisabledEntry(source);

    private static string? ReadPackageVersion(string packageRoot)
    {
        try
        {
            var manifestPath = Path.Combine(packageRoot, "package.json");
            if (!File.Exists(manifestPath))
            {
                return null;
            }

            return JsonNode.Parse(File.ReadAllText(manifestPath))?["version"]?.GetValue<string>();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>扩展来自哪个包：优先用包的根目录名（npm 作用域名要连作用域一起保留）。</summary>
    private static string PluginLabel(SourceInfo info)
    {
        if (!string.IsNullOrEmpty(info.BaseDir))
        {
            var normalized = info.BaseDir.Replace('\\', '/').TrimEnd('/');
            const string marker = "/node_modules/";
            var index = normalized.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index >= 0)
            {
                return normalized[(index + marker.Length)..];
            }

            return normalized.Split('/').Last();
        }

        var path = info.Path.Replace('\\', '/').TrimEnd('/');
        return path.Split('/').Last();
    }

    private static string ExposureText(string exposure) => exposure switch
    {
        ToolExposure.Codemode => "codemode",
        ToolExposure.Deferred => "deferred · 需 tool_search",
        ToolExposure.Hidden => "hidden",
        ToolExposure.ModelOnly => "model-only",
        _ => "direct",
    };

    private DefaultPackageManager CreateManager(out SettingsManager settingsManager)
    {
        var cwd = ResolveWorkspace();
        var agentDir = Config.GetAgentDir();
        settingsManager = SettingsManager.Create(cwd, agentDir, new SettingsManagerCreateOptions
        {
            // 项目级包只在受信任工作区里装；用户级包不受影响。
            ProjectTrusted = _settings.Current.TrustWorkspace,
        });

        return new DefaultPackageManager(
            settingsManager,
            cwd,
            agentDir,
            commandRunner: new PiPackageCommandRunner(_settings.Current.NpmRegistry));
    }

    private string ResolveWorkspace() => PlainWorkspace.Resolve(_settings.Current.WorkspacePath);

    private static string? DescribeProgress(PackageProgressEvent evt)
    {
        if (!string.IsNullOrWhiteSpace(evt.Message))
        {
            // npm 的 message 有时带整段多行输出，卡片只放两行，取第一行。
            return FirstLine(evt.Message);
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

    /// <summary>npm 不在 PATH 上时，Win32Exception 的原话是英文而且只说「找不到文件」，得翻译成人话。</summary>
    private static bool IsMissingNpm(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current.Message.Contains("npm", StringComparison.OrdinalIgnoreCase)
                && (current.Message.Contains("cannot find", StringComparison.OrdinalIgnoreCase)
                    || current.Message.Contains("找不到", StringComparison.Ordinal)
                    || current.Message.Contains("No such file", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
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

    /// <summary>包条目可能是字符串，也可能是带过滤 / 启停的对象形态。</summary>
    public static string? PackageSourceOf(JsonNode? node) => node switch
    {
        JsonValue value when value.TryGetValue<string>(out var text) => text,
        JsonObject obj when obj["source"] is JsonValue source
                             && source.TryGetValue<string>(out var text) => text,
        _ => null,
    };

    /// <summary>四类资源都写成空数组才算「停用」；只关扩展的对象形态不算整体停用。</summary>
    public static bool IsDisabledEntry(JsonNode? node)
        => node is JsonObject obj
           && IsEmptyArray(obj["extensions"])
           && IsEmptyArray(obj["skills"])
           && IsEmptyArray(obj["prompts"])
           && IsEmptyArray(obj["themes"]);

    /// <summary>
    /// 停用用的条目形态：pi 对空数组的语义是「这一类全不要」，
    /// 四条都写空就等于这个包一行都不加载，但记录还在清单里，能再开回来。
    /// </summary>
    public static JsonObject DisabledEntry(string source) => new()
    {
        ["source"] = source,
        ["extensions"] = new JsonArray(),
        ["skills"] = new JsonArray(),
        ["prompts"] = new JsonArray(),
        ["themes"] = new JsonArray(),
    };

    private static bool IsEmptyArray(JsonNode? node) => node is JsonArray { Count: 0 };

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
    /// 一个包会贡献哪些扩展入口。规则必须和 pi 的包发现一模一样，否则界面会报「扩展 1 个」而会话里一个都没加载：
    /// 有 <c>pi</c> 清单时**只认清单**（清单里没写 extensions 就是没有，不回落约定目录）；
    /// 没有清单时只看 <c>extensions/</c> 目录；根目录的 <c>index.ts</c> 只对手动放置的扩展目录生效，对包不生效。
    /// </summary>
    public static IReadOnlyList<string> FindExtensions(string packageRoot)
    {
        if (!Directory.Exists(packageRoot))
        {
            return [];
        }

        try
        {
            if (ReadManifest(packageRoot) is { } manifest)
            {
                return manifest["extensions"] is JsonArray entries
                    ? ToRelative(packageRoot, ExpandEntries(packageRoot, entries))
                    : [];
            }

            var conventional = Path.Combine(packageRoot, "extensions");
            return Directory.Exists(conventional)
                ? ToRelative(packageRoot, CollectExtensionDir(conventional))
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    /// <summary>读 <c>pi</c> 清单；<c>pi</c> 不是对象就当没有。对应上游 <c>readPiManifest</c>。</summary>
    private static JsonObject? ReadManifest(string directory)
    {
        var manifestPath = Path.Combine(directory, "package.json");
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var root = JsonNode.Parse(File.ReadAllText(manifestPath));
        return root?["pi"] as JsonObject;
    }

    /// <summary>
    /// 清单条目展开成绝对路径。<c>+</c>/<c>-</c>/<c>!</c> 是启用 / 禁用覆盖，
    /// 决定的是「这些扩展开没开」，不决定「有哪些扩展」，这里跳过。
    /// </summary>
    private static IReadOnlyList<string> ExpandEntries(string baseDir, JsonArray entries)
    {
        var results = new List<string>();
        foreach (var node in entries)
        {
            if (node is not JsonValue value
                || !value.TryGetValue<string>(out var raw)
                || raw is null)
            {
                continue;
            }

            var pattern = StripDotSlash(raw.Trim());
            if (pattern.Length == 0 || pattern[0] is '+' or '-' or '!')
            {
                continue;
            }

            pattern = pattern.Replace('\\', '/');
            if (!HasGlob(pattern))
            {
                var full = Path.GetFullPath(Path.Combine(baseDir, pattern.Replace('/', Path.DirectorySeparatorChar)));
                if (File.Exists(full))
                {
                    if (IsExtensionFile(full))
                    {
                        results.Add(full);
                    }
                }
                else if (Directory.Exists(full))
                {
                    results.AddRange(CollectExtensionDir(full));
                }

                continue;
            }

            var regex = GlobToRegex(pattern);
            foreach (var item in EnumerateEntries(baseDir))
            {
                if (!regex.IsMatch(Relative(baseDir, item)))
                {
                    continue;
                }

                if (Directory.Exists(item))
                {
                    results.AddRange(CollectExtensionDir(item));
                }
                else if (IsExtensionFile(item))
                {
                    results.Add(item);
                }
            }
        }

        return results;
    }

    /// <summary>
    /// 对应上游 <c>collectAutoExtensionEntries</c>：目录自己先当入口（清单或 index.*），
    /// 否则只往下看一层——文件按扩展名收，子目录再各自找入口。
    /// </summary>
    private static IReadOnlyList<string> CollectExtensionDir(string directory)
    {
        if (ResolveDirectoryEntries(directory) is { } own)
        {
            return own;
        }

        var results = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if (IsExtensionFile(file))
            {
                results.Add(file);
            }
        }

        foreach (var child in Directory.EnumerateDirectories(directory))
        {
            var name = Path.GetFileName(child);
            if (IsSkippedDirectory(name))
            {
                continue;
            }

            if (ResolveDirectoryEntries(child) is { } entries)
            {
                results.AddRange(entries);
            }
        }

        return results;
    }

    /// <summary>目录自己的入口：<c>package.json</c> 里的清单，或者 index.ts / index.js / index.dll。</summary>
    private static IReadOnlyList<string>? ResolveDirectoryEntries(string directory)
    {
        if (ReadManifest(directory) is { } manifest && manifest["extensions"] is JsonArray entries)
        {
            var expanded = ExpandEntries(directory, entries);
            if (expanded.Count > 0)
            {
                return expanded;
            }
        }

        return ResolveDirectoryEntry(directory) is { } entry ? [entry] : null;
    }

    /// <summary>递归列出目录项，跳过点目录与 node_modules；上限防止一个包把整块磁盘拖进来。</summary>
    private static IReadOnlyList<string> EnumerateEntries(string root)
    {
        var results = new List<string>();
        var queue = new Queue<(string Directory, int Depth)>();
        queue.Enqueue((root, 0));

        while (queue.Count > 0 && results.Count < 4000)
        {
            var (directory, depth) = queue.Dequeue();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var entry in entries)
            {
                if (Directory.Exists(entry))
                {
                    if (IsSkippedDirectory(Path.GetFileName(entry)))
                    {
                        continue;
                    }

                    results.Add(entry);
                    if (depth < 8)
                    {
                        queue.Enqueue((entry, depth + 1));
                    }
                }
                else
                {
                    results.Add(entry);
                }
            }
        }

        return results;
    }

    private static bool IsSkippedDirectory(string name)
        => name.StartsWith('.') || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase);

    /// <summary>只去掉开头的 <c>./</c>；不能 TrimStart('.')，那会把 <c>./.pi/x.ts</c> 变成 <c>pi/x.ts</c>。</summary>
    private static string StripDotSlash(string value)
        => value.StartsWith("./", StringComparison.Ordinal) || value.StartsWith(".\\", StringComparison.Ordinal)
            ? value[2..]
            : value;

    private static IReadOnlyList<string> ToRelative(string root, IReadOnlyList<string> paths)
        => paths.Select(path => Relative(root, path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

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
