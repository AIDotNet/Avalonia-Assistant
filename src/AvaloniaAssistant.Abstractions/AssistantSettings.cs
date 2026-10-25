using System.Text.Json.Serialization;

namespace AvaloniaAssistant.Abstractions;

/// <summary>模型服务协议。运行时据此选择 pi.net 的 provider 适配器。</summary>
public enum AgentProtocol
{
    /// <summary>OpenAI Chat Completions（/chat/completions），兼容面最广。</summary>
    OpenAiCompatible,

    /// <summary>OpenAI Responses（/responses），官方新接口。</summary>
    OpenAiResponses,

    /// <summary>Anthropic Messages（/messages），Claude 原生形状。</summary>
    AnthropicMessages,
}

/// <summary>桌面端可选的模型服务预设。只影响新建模型时的默认值和协议推荐，不限制你能填什么。</summary>
public sealed record ProviderPreset(
    AgentProviderKind Kind,
    string Name,
    string Description,
    string BaseUrl,
    string Model,
    AgentProtocol Protocol)
{
    public static readonly ProviderPreset DeepSeek = new(
        AgentProviderKind.DeepSeek,
        "DeepSeek",
        "OpenAI 兼容接口，国内直连",
        "https://api.deepseek.com/v1",
        "deepseek-flash",
        AgentProtocol.OpenAiCompatible);

    public static readonly ProviderPreset OpenAI = new(
        AgentProviderKind.OpenAI,
        "OpenAI",
        "官方接口，可选 Chat / Responses",
        "https://api.openai.com/v1",
        "gpt-6.1-sol",
        AgentProtocol.OpenAiCompatible);

    public static readonly ProviderPreset Anthropic = new(
        AgentProviderKind.Anthropic,
        "Anthropic",
        "Claude Messages 接口",
        "https://api.anthropic.com",
        "claude-sonnet-5-5",
        AgentProtocol.AnthropicMessages);

    public static readonly ProviderPreset Custom = new(
        AgentProviderKind.Custom,
        "自定义",
        "任意 OpenAI 兼容端点（vLLM / Ollama / One-API / 公司网关…）",
        "http://localhost:11434/v1",
        "qwen3.8:27b",
        AgentProtocol.OpenAiCompatible);

    public static readonly IReadOnlyList<ProviderPreset> All = [DeepSeek, OpenAI, Anthropic, Custom];

    public static ProviderPreset For(AgentProviderKind kind)
        => All.FirstOrDefault(preset => preset.Kind == kind) ?? DeepSeek;
}

public enum AgentProviderKind
{
    DeepSeek,
    OpenAI,
    Anthropic,
    Custom,
}

/// <summary>
/// 一套模型配置：渠道（BaseUrl + Key）、模型名和协议各自独立。
/// <para>
/// 以前这些东西是全局单份的，界面还要靠一张写死的「服务商 → 模型」对照表来猜有哪些模型，
/// 于是连着公司网关、只配了 deepseek-flash，列表里却能列出三个根本不存在于该端点的 GPT 模型。
/// 现在一个模型就是一条配置，列表直接来自这里。
/// </para>
/// </summary>
public sealed class ModelProfile
{
    /// <summary>稳定标识。界面的选中态、会话的「当前模型」都认它，改名字不会丢选中。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>显示名。留空就显示模型名，多个同模型不同渠道时才需要另起名字。</summary>
    public string Name { get; set; } = string.Empty;

    public AgentProviderKind Provider { get; set; } = AgentProviderKind.DeepSeek;

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string Model { get; set; } = string.Empty;

    /// <summary>null 表示跟随服务商预设：Anthropic 走 Messages，其余走 Chat Completions。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentProtocol? Protocol { get; set; }

    /// <summary>上下文窗口覆盖。null 表示按本地模型目录查，查不到再用保守值。</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ContextWindow { get; set; }

    [JsonIgnore]
    public AgentProtocol EffectiveProtocol => Protocol ?? ProviderPreset.For(Provider).Protocol;

    [JsonIgnore]
    public string ProviderName => ProviderPreset.For(Provider).Name;

    /// <summary>界面上那个名字：没起名就用模型名。</summary>
    [JsonIgnore]
    public string DisplayName
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Name))
            {
                return Name.Trim();
            }

            return string.IsNullOrWhiteSpace(Model) ? "未命名模型" : Model.Trim();
        }
    }

    public static string NewId() => "m" + Guid.NewGuid().ToString("n")[..8];

    /// <summary>按预设新建一套配置。model / baseUrl 留空时由 <see cref="AssistantSettings.NormalizeModels"/> 填默认值。</summary>
    public static ModelProfile Create(AgentProviderKind kind, string? name = null, bool usePresetDefaults = false)
    {
        var preset = ProviderPreset.For(kind);
        return new ModelProfile
        {
            Id = NewId(),
            Name = name ?? string.Empty,
            Provider = kind,
            BaseUrl = usePresetDefaults ? preset.BaseUrl : string.Empty,
            Model = usePresetDefaults ? preset.Model : string.Empty,
            Protocol = null,
        };
    }

    /// <summary>
    /// 从旧版 settings.json 的扁平字段还原一套配置。
    /// <para>
    /// 地址和预设对不上时标成「自定义」：那种情况实际连的是自建网关或代理，
    /// 界面上继续写着「OpenAI」只会让人以为列表里的 GPT 模型真能用。
    /// </para>
    /// </summary>
    public static ModelProfile FromLegacy(
        string? provider,
        string? baseUrl,
        string? apiKey,
        string? model,
        string? protocol)
    {
        var kind = Enum.TryParse<AgentProviderKind>(provider, ignoreCase: true, out var parsedKind)
            ? parsedKind
            : AgentProviderKind.DeepSeek;
        var preset = ProviderPreset.For(kind);
        var url = (baseUrl ?? string.Empty).Trim();
        var name = (model ?? string.Empty).Trim();

        // 协议先认旧文件里写过的；没写过就跟随原服务商推荐——地址换了不代表协议换了
        var hasExplicitProtocol = Enum.TryParse<AgentProtocol>(protocol, ignoreCase: true, out var parsedProtocol);

        var isForeignEndpoint = url.Length > 0 && !SameEndpoint(url, preset.BaseUrl);
        if (isForeignEndpoint)
        {
            kind = AgentProviderKind.Custom;
        }

        var resolvedProtocol = hasExplicitProtocol
            ? parsedProtocol
            : kind == AgentProviderKind.Custom && preset.Kind != kind
                ? preset.Protocol
                : (AgentProtocol?)null;

        return new ModelProfile
        {
            Id = NewId(),
            Name = name,
            Provider = kind,
            BaseUrl = url,
            ApiKey = (apiKey ?? string.Empty).Trim(),
            Model = name,
            Protocol = resolvedProtocol,
        };
    }

    public ModelProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        Provider = Provider,
        BaseUrl = BaseUrl,
        ApiKey = ApiKey,
        Model = Model,
        Protocol = Protocol,
        ContextWindow = ContextWindow,
    };

    /// <summary>逐字段比较。设置页靠它判断「改过没有」，所以 Key 也要比。</summary>
    public bool SameAs(ModelProfile other)
        => Id == other.Id
           && string.Equals(Name, other.Name, StringComparison.Ordinal)
           && Provider == other.Provider
           && string.Equals(BaseUrl, other.BaseUrl, StringComparison.Ordinal)
           && string.Equals(ApiKey, other.ApiKey, StringComparison.Ordinal)
           && string.Equals(Model, other.Model, StringComparison.Ordinal)
           && Protocol == other.Protocol
           && ContextWindow == other.ContextWindow;

    private static bool SameEndpoint(string left, string right)
        => string.Equals(
            left.Trim().TrimEnd('/'),
            right.Trim().TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
}

public enum PermissionMode
{
    /// <summary>只能读取工作区，所有写入与命令都被拒绝。</summary>
    ReadOnly,

    /// <summary>默认权限：工作区可读写，命令与越界写入需要用户逐次确认。</summary>
    Workspace,

    /// <summary>完全访问：全部工具直接执行，不再逐次确认。</summary>
    FullAccess,
}

public static class PermissionModeText
{
    public static string Describe(PermissionMode mode) => mode switch
    {
        PermissionMode.ReadOnly => "只读",
        PermissionMode.FullAccess => "完全访问",
        _ => "工作区可写",
    };

    public static string Detail(PermissionMode mode) => mode switch
    {
        PermissionMode.ReadOnly => "只能读取工作区内容，不修改文件、不执行命令。",
        PermissionMode.FullAccess => "所有工具直接执行，不再逐次确认。请注意数据安全，建议只交给可信任的任务。",
        _ => "读写工作区内的文件；执行命令、调用 MCP 工具之前会先问你。",
    };

    /// <summary>该权限下是否跳过逐次授权（完全访问直接放行）。</summary>
    public static bool SkipsApproval(PermissionMode mode) => mode == PermissionMode.FullAccess;
}

/// <summary>本地设置。持久化在 %APPDATA%/AvaloniaAssistant/settings.json。</summary>
public sealed class AssistantSettings
{
    /// <summary>
    /// 配好的模型。一个文件里可以有很多套，各走各的端点、Key 和协议。
    /// 旧版 settings.json 里的单套配置由 <see cref="ModelProfile.FromLegacy"/> 搬进来。
    /// </summary>
    public List<ModelProfile> Models { get; set; } = [];

    /// <summary>当前生效的模型 id。找不到时回落到第一个，不让界面出现「一个都没选」的空档。</summary>
    public string? ActiveModelId { get; set; }

    /// <summary>当前生效的那套配置。没有配置时为 null。</summary>
    [JsonIgnore]
    public ModelProfile? ActiveModel
    {
        get
        {
            var models = Models;
            if (models.Count == 0)
            {
                return null;
            }

            var active = models.FirstOrDefault(item => string.Equals(item.Id, ActiveModelId, StringComparison.Ordinal));
            if (active is null)
            {
                active = models[0];
                ActiveModelId = active.Id;
            }

            return active;
        }
    }

    /// <summary>当前模型的协议，已经算上「跟随预设」。</summary>
    [JsonIgnore]
    public AgentProtocol EffectiveProtocol => ActiveModel?.EffectiveProtocol ?? ProviderPreset.DeepSeek.Protocol;

    [JsonIgnore]
    public ProtocolOption ProtocolInfo => ProtocolCatalog.For(EffectiveProtocol);

    // ---- 兼容旧调用点：这些属性都指向「当前生效的那套配置」 ----

    [JsonIgnore]
    public AgentProviderKind Provider
    {
        get => ActiveModel?.Provider ?? AgentProviderKind.DeepSeek;
        set => EnsureActive().Provider = value;
    }

    [JsonIgnore]
    public string ApiKey
    {
        get => ActiveModel?.ApiKey ?? string.Empty;
        set => EnsureActive().ApiKey = value ?? string.Empty;
    }

    [JsonIgnore]
    public string BaseUrl
    {
        get => ActiveModel?.BaseUrl ?? string.Empty;
        set => EnsureActive().BaseUrl = value ?? string.Empty;
    }

    [JsonIgnore]
    public string Model
    {
        get => ActiveModel?.Model ?? string.Empty;
        set => EnsureActive().Model = value ?? string.Empty;
    }

    [JsonIgnore]
    public AgentProtocol? Protocol
    {
        get => ActiveModel?.Protocol;
        set => EnsureActive().Protocol = value;
    }

    public double Temperature { get; set; } = 0.7;

    public int MaxTokens { get; set; } = 4096;

    /// <summary>语音转写模型；仅在端点支持 /audio/transcriptions 时可用。</summary>
    public string SpeechModel { get; set; } = "whisper-1";

    public PermissionMode Permission { get; set; } = PermissionMode.Workspace;

    /// <summary>当前任务默认工作区；为空时使用用户主目录下的 AvaloniaAssistant/Workspace。</summary>
    public string WorkspacePath { get; set; } = string.Empty;

    /// <summary>
    /// 是否信任工作区里的 <c>.pi</c> 配置（技能、MCP 服务端、AGENTS.md）。
    /// 桌面助理的工作区由用户自己选择，默认信任；把助理指向来路不明的目录时应关掉。
    /// </summary>
    public bool TrustWorkspace { get; set; } = true;

    public string UserName { get; set; } = Environment.UserName;

    public List<string> RecentWorkspaces { get; set; } = [];

    /// <summary>被收起的工作区；没记在这里的一律按展开处理。</summary>
    public List<string> CollapsedWorkspaces { get; set; } = [];

    public List<string> DisabledTools { get; set; } = [];

    /// <summary>
    /// 被关掉的技能名。技能本体是磁盘上的 SKILL.md，这里只决定要不要让它进系统提示，
    /// 不动文件——用户想彻底不要它，自己去目录删。
    /// </summary>
    public List<string> DisabledSkills { get; set; } = [];

    public string Mode { get; set; } = "office";

    /// <summary>启动时打开哪一页：<c>home</c> 首页，<c>last</c> 上次那条任务。</summary>
    public string StartupPage { get; set; } = "home";

    /// <summary>上次打开的会话 id。启动恢复只认它还存在于 sessions 目录里的情况。</summary>
    public string? LastSessionId { get; set; }

    public bool IsSidebarVisible { get; set; } = true;

    /// <summary>侧栏宽度（DIP）。拖动分隔条后落盘，下次启动还是这个宽度。</summary>
    public double SidebarWidth { get; set; } = 268;

    /// <summary>界面缩放，1 表示 100%。只缩放渲染层，不改系统 DPI。</summary>
    public double FontScale { get; set; } = 1;

    /// <summary>窗口尺寸与位置。关掉再开应该回到原来的地方，而不是每次都居中。</summary>
    public double WindowWidth { get; set; } = 1280;

    public double WindowHeight { get; set; } = 820;

    public int? WindowLeft { get; set; }

    public int? WindowTop { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>
    /// 把当前生效的模型切到 <paramref name="profileId"/>。找不到就什么也不做，
    /// 返回 false 让调用方知道没切成功（比如那条配置刚在设置页被删掉）。
    /// </summary>
    public bool ActivateModel(string? profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return false;
        }

        if (!Models.Any(item => string.Equals(item.Id, profileId, StringComparison.Ordinal)))
        {
            return false;
        }

        ActiveModelId = profileId;
        return true;
    }

    /// <summary>
    /// 收口所有「这套配置是否完整」的默认值：空模型名 / 空地址按预设补、丢掉空 id、保证至少一套。
    /// 每次落盘和读盘都调，保证运行时不面对半套配置。
    /// </summary>
    public void NormalizeModels()
    {
        var models = Models;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var profile in models)
        {
            if (string.IsNullOrWhiteSpace(profile.Id) || !seen.Add(profile.Id))
            {
                profile.Id = ModelProfile.NewId();
                seen.Add(profile.Id);
            }

            profile.Name = (profile.Name ?? string.Empty).Trim();
            profile.ApiKey = (profile.ApiKey ?? string.Empty).Trim();
            profile.BaseUrl = (profile.BaseUrl ?? string.Empty).Trim();
            profile.Model = (profile.Model ?? string.Empty).Trim();

            var preset = ProviderPreset.For(profile.Provider);
            if (profile.Model.Length == 0)
            {
                profile.Model = preset.Model;
            }

            if (profile.BaseUrl.Length == 0)
            {
                profile.BaseUrl = preset.BaseUrl;
            }

            if (profile.ContextWindow is <= 0)
            {
                profile.ContextWindow = null;
            }
        }

        if (models.Count == 0)
        {
            models.Add(ModelProfile.Create(AgentProviderKind.DeepSeek, usePresetDefaults: true));
        }

        if (string.IsNullOrWhiteSpace(ActiveModelId)
            || !models.Any(item => string.Equals(item.Id, ActiveModelId, StringComparison.Ordinal)))
        {
            ActiveModelId = models[0].Id;
        }
    }

    public AssistantSettings Clone() => new()
    {
        Models = Models.Select(profile => profile.Clone()).ToList(),
        ActiveModelId = ActiveModelId,
        Temperature = Temperature,
        MaxTokens = MaxTokens,
        SpeechModel = SpeechModel,
        Permission = Permission,
        WorkspacePath = WorkspacePath,
        TrustWorkspace = TrustWorkspace,
        UserName = UserName,
        RecentWorkspaces = [.. RecentWorkspaces],
        CollapsedWorkspaces = [.. CollapsedWorkspaces],
        DisabledTools = [.. DisabledTools],
        DisabledSkills = [.. DisabledSkills],
        Mode = Mode,
        StartupPage = StartupPage,
        LastSessionId = LastSessionId,
        IsSidebarVisible = IsSidebarVisible,
        SidebarWidth = SidebarWidth,
        FontScale = FontScale,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        WindowLeft = WindowLeft,
        WindowTop = WindowTop,
        WindowMaximized = WindowMaximized,
    };

    private ModelProfile EnsureActive()
    {
        if (ActiveModel is { } active)
        {
            return active;
        }

        var created = ModelProfile.Create(AgentProviderKind.DeepSeek, usePresetDefaults: true);
        Models.Add(created);
        ActiveModelId = created.Id;
        return created;
    }

}
