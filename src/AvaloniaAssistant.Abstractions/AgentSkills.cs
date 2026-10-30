namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 技能市场里的一个技能。数据来自 skills.sh 的搜索接口
/// （<c>GET /api/search?q=…</c>），描述再从技能详情页的 meta 里补一次。
/// </summary>
public sealed record AgentSkillMarketItem(
    string Source,
    string SkillId,
    string Name,
    int Installs,
    string Description,
    string Url)
{
    /// <summary>安装时传给 CLI 的形式：<c>owner/repo</c> + <c>--skill name</c>。</summary>
    public string Slug => $"{Source}@{SkillId}";

    /// <summary>安装量，按 skills.sh 自己的写法缩写（1.2M / 264.7K / 983）。</summary>
    public string InstallsText => Installs switch
    {
        >= 1_000_000 => $"{Installs / 1_000_000d:0.#}M",
        >= 1_000 => $"{Installs / 1_000d:0.#}K",
        _ => Installs.ToString(),
    };
}

/// <summary>一次搜索的结果。<see cref="Items"/> 已按安装量从高到低排好。</summary>
public sealed record AgentSkillSearchResult(IReadOnlyList<AgentSkillMarketItem> Items)
{
    public static readonly AgentSkillSearchResult Empty = new([]);
}

/// <summary>装一个技能的结果。<see cref="Message"/> 是给人看的，不是异常栈。</summary>
public sealed record AgentSkillInstallResult(bool Success, string Message);

/// <summary>
/// 技能市场。搜索走 skills.sh 的公开接口，安装交给官方 CLI（<c>npx skills add</c>）——
/// 技能在仓库里的路径是任意的（<c>skills/engineering/tdd/</c> 这种），自己拼 raw URL 拼不出来。
/// </summary>
public interface ISkillMarket
{
    /// <summary>按关键词搜。空关键词取「最热」那一屏。</summary>
    Task<AgentSkillSearchResult> SearchAsync(
        string query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 装到 <paramref name="skillsRoot"/> 下面。CLI 会先下到临时目录的
    /// <c>.agents/skills/&lt;name&gt;</c>，这里再整体拷过去，源目录留着给别的 agent 用。
    /// </summary>
    Task<AgentSkillInstallResult> InstallAsync(
        AgentSkillMarketItem item,
        string skillsRoot,
        CancellationToken cancellationToken = default);
}
