using System.Diagnostics;
using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AvaloniaAssistant.Abstractions;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// skills.sh 的读写两头：
/// <list type="bullet">
/// <item>搜索走 <c>GET /api/search?q=…&amp;limit=20</c>——这是官方 CLI 自己用的接口，
/// 返回 <c>id / source / skillId / installs</c>，但<b>没有描述</b>；</item>
/// <item>描述逐个从技能详情页的 <c>&lt;meta name="description"&gt;</c> 补，那正是 SKILL.md
/// 里 frontmatter 的 description；</item>
/// <item>安装不自己拼 raw URL：技能在仓库里的路径是任意的（<c>skills/engineering/tdd/</c>），
/// 由官方 CLI 解析最稳，装完再整目录拷进本应用的技能目录。</item>
/// </list>
/// </summary>
public sealed partial class SkillMarketStore(ISettingsProvider settings) : ISkillMarket
{
    private const string SearchEndpoint = "https://skills.sh/api/search";
    private const string SiteBase = "https://skills.sh";

    /// <summary>接口上限 20；再多也没意义，一屏放不下。</summary>
    private const int ResultLimit = 20;

    /// <summary>描述要一页一页抓，并发 6 个比较稳：20 条大约一两秒。</summary>
    private const int DescriptionConcurrency = 6;

    /// <summary>下面这个接口对少于 2 个字符的查询直接返回 400。</summary>
    private const int MinQueryLength = 2;

    private readonly HttpClient _http = CreateHttp();

    private static HttpClient CreateHttp()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AvaloniaAssistant/1.0 (skills market)");
        return http;
    }

    public async Task<AgentSkillSearchResult> SearchAsync(
        string query,
        CancellationToken cancellationToken = default)
    {
        var keyword = (query ?? string.Empty).Trim();
        if (keyword.Length < MinQueryLength)
        {
            return AgentSkillSearchResult.Empty;
        }

        var url = $"{SearchEndpoint}?q={Uri.EscapeDataString(keyword)}&limit={ResultLimit}";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return AgentSkillSearchResult.Empty;
        }

        await using var stream = await response.Content
            .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var root = await JsonNode
            .ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (root?["skills"] is not JsonArray array)
        {
            return AgentSkillSearchResult.Empty;
        }

        var items = new List<AgentSkillMarketItem>(array.Count);
        foreach (var entry in array)
        {
            var source = ReadString(entry?["source"]);
            var skillId = ReadString(entry?["skillId"]);
            if (source.Length == 0 || skillId.Length == 0)
            {
                continue;
            }

            var name = ReadString(entry?["name"]);
            var installs = entry?["installs"]?.GetValue<int>() ?? 0;

            items.Add(new AgentSkillMarketItem(
                source,
                skillId,
                name.Length == 0 ? skillId : name,
                installs,
                string.Empty,
                $"{SiteBase}/{source}/{skillId}"));
        }

        var filled = await FillDescriptionsAsync(items, cancellationToken).ConfigureAwait(false);
        return new AgentSkillSearchResult(
            filled.OrderByDescending(item => item.Installs).ToList());
    }

    /// <summary>搜索接口不带描述，逐个去详情页补。抓不到就留空，不影响列表。</summary>
    private async Task<List<AgentSkillMarketItem>> FillDescriptionsAsync(
        List<AgentSkillMarketItem> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return items;
        }

        using var gate = new SemaphoreSlim(DescriptionConcurrency);
        var slots = new AgentSkillMarketItem?[items.Count];

        var tasks = items.Select(async (item, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var description = await FetchDescriptionAsync(item.Url, cancellationToken)
                    .ConfigureAwait(false);
                slots[index] = description.Length == 0 ? item : item with { Description = description };
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return slots.Where(slot => slot is not null).Select(slot => slot!).ToList();
    }

    private async Task<string> FetchDescriptionAsync(string url, CancellationToken cancellationToken)
    {
        try
        {
            var html = await _http.GetStringAsync(url, cancellationToken).ConfigureAwait(false);
            return ExtractDescription(html);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // 详情页抓不到只是少了句描述，别让整条搜索失败。
            return string.Empty;
        }
    }

    /// <summary>
    /// 页面里同名 meta 有好几个（description / og:description / twitter:description），
    /// 取第一个 name="description" 就是 SKILL.md 那份原文。
    /// </summary>
    private static string ExtractDescription(string html)
    {
        foreach (Match meta in MetaTagPattern().Matches(html))
        {
            var tag = meta.Value;
            if (!tag.Contains("name=\"description\"", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = ContentAttributePattern().Match(tag);
            if (content.Success)
            {
                return WebUtility.HtmlDecode(content.Groups[1].Value).Trim();
            }
        }

        return string.Empty;
    }

    public async Task<AgentSkillInstallResult> InstallAsync(
        AgentSkillMarketItem item,
        string skillsRoot,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(skillsRoot))
        {
            return new AgentSkillInstallResult(false, "技能目录不可用，先选一个工作区。");
        }

        // 用一个一次性目录当 cwd：CLI 会把技能落到 <cwd>/.agents/skills/<name>，
        // 顺带写一份 skills-lock.json，都留在临时目录里，不碰用户的项目。
        var sandbox = Path.Combine(
            Path.GetTempPath(),
            "avalonia-skill-" + Guid.NewGuid().ToString("N")[..8]);

        try
        {
            Directory.CreateDirectory(sandbox);

            var (exitCode, output) = RunNpx(sandbox, item, cancellationToken);
            if (exitCode is not 0)
            {
                // CLI 在非 TTY 下有时把错误写到 stdout，两个都带上一句才好排查。
                return new AgentSkillInstallResult(
                    false,
                    $"npx 退出码 {exitCode}：{(output.Length == 0 ? "没有输出" : output.Trim())}");
            }

            var landed = Path.Combine(sandbox, ".agents", "skills");
            var directories = Directory.Exists(landed)
                ? Directory.GetDirectories(landed)
                : [];

            if (directories.Length == 0)
            {
                return new AgentSkillInstallResult(
                    false,
                    "装完了但没在落地目录里找到技能，多半是仓库结构变了或网络中断。");
            }

            // 正常只会有一个；多个时取名字最接近的那个，免得把整仓库搬进来。
            var source = directories.FirstOrDefault(directory =>
                    string.Equals(
                        Path.GetFileName(directory),
                        ToSlug(item.SkillId),
                        StringComparison.OrdinalIgnoreCase))
                ?? directories[0];

            var target = Path.Combine(skillsRoot, Path.GetFileName(source));
            CopyDirectory(source, target);

            return new AgentSkillInstallResult(true, $"已装到 {target}");
        }
        catch (Exception exception)
        {
            return new AgentSkillInstallResult(false, "装不上：" + exception.Message);
        }
        finally
        {
            TryDelete(sandbox);
        }
    }

    /// <summary>
    /// 自己起进程，不走 <c>PiPackageCommandRunner</c>：那个执行器把指向 127.0.0.1 的代理当成
    /// 「别的宿主注入的临时端口」摘掉，可用户的 Clash 就开在本机回环上——而 skills CLI 要访问
    /// skills.sh 和 GitHub，摘掉代理就直接 exit 1。这里只换 npm 源，代理原样继承。
    /// </summary>
    private (int ExitCode, string Output) RunNpx(
        string workingDirectory,
        AgentSkillMarketItem item,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var result = ChildProcess.SpawnSync(
            OperatingSystem.IsWindows() ? "npx.cmd" : "npx",
            ["-y", "skills", "add", item.Source, "--skill", item.SkillId, "-y"],
            new SpawnSyncOptions
            {
                Cwd = workingDirectory,
                TimeoutMs = 180_000,
                WindowsHide = true,
                // 空覆盖层 = 继承父进程环境（含代理），只把 registry 换成设置里的镜像。
                Env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["npm_config_registry"] = settings.Current.NpmRegistry,
                },
            });

        return (result.Status ?? -1, (result.Stderr ?? string.Empty) + (result.Stdout ?? string.Empty));
    }

    /// <summary>CLI 落盘时会把名字转成 slug（小写、空格改连字符）。</summary>
    private static string ToSlug(string name)
    {
        var lowered = name.ToLowerInvariant().Replace(' ', '-').Replace('_', '-');
        var cleaned = SlugNoisePattern().Replace(lowered, "-");
        return cleaned.Trim('-');
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // 临时目录删不掉不影响结果，交给系统清理。
        }
    }

    private static string ReadString(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    [GeneratedRegex("<meta\\s[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex MetaTagPattern();

    [GeneratedRegex("content=\"([^\"]*)\"", RegexOptions.IgnoreCase)]
    private static partial Regex ContentAttributePattern();

    [GeneratedRegex("[^a-z0-9-]+")]
    private static partial Regex SlugNoisePattern();
}
