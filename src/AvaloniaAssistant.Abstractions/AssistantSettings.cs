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

/// <summary>桌面端可选的模型服务预设。</summary>
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
        "任意 OpenAI 兼容端点（vLLM / Ollama / One-API…）",
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
    public AgentProviderKind Provider { get; set; } = AgentProviderKind.DeepSeek;

    public string ApiKey { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = ProviderPreset.DeepSeek.BaseUrl;

    public string Model { get; set; } = ProviderPreset.DeepSeek.Model;

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
    /// 界面上显式选择的协议；null 表示跟随服务商预设。
    /// 旧版 settings.json 没有这个字段，读出来仍是 null，不会把 Anthropic 预设误降级成 Chat。
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentProtocol? Protocol { get; set; }

    [JsonIgnore]
    public AgentProtocol EffectiveProtocol => Protocol ?? ProviderPreset.For(Provider).Protocol;

    [JsonIgnore]
    public ProtocolOption ProtocolInfo => ProtocolCatalog.For(EffectiveProtocol);

    public AssistantSettings Clone() => new()
    {
        Provider = Provider,
        ApiKey = ApiKey,
        BaseUrl = BaseUrl,
        Model = Model,
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
        Protocol = Protocol,
    };
}
