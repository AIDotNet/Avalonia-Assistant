using System.Text.Json.Serialization;

namespace AvaloniaAssistant.Abstractions;

/// <summary>模型服务协议。运行时据此选择 pi.net 的 provider 适配器。</summary>
public enum AgentProtocol
{
    OpenAiCompatible,
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
        "deepseek-v4-flash",
        AgentProtocol.OpenAiCompatible);

    public static readonly ProviderPreset OpenAI = new(
        AgentProviderKind.OpenAI,
        "OpenAI",
        "官方 OpenAI 接口",
        "https://api.openai.com/v1",
        "gpt-5.5",
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
        "qwen3.5:9b",
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

    /// <summary>工作区内可读写，命令与越界写入需要用户逐次确认。</summary>
    Workspace,

    /// <summary>全部工具直接执行，不再逐次确认。</summary>
    FullAccess,
}

public static class PermissionModeText
{
    public static string Describe(PermissionMode mode) => mode switch
    {
        PermissionMode.ReadOnly => "只读",
        PermissionMode.FullAccess => "完全访问",
        _ => "工作区",
    };

    public static string Detail(PermissionMode mode) => mode switch
    {
        PermissionMode.ReadOnly => "只能读取工作区内容，不会修改任何文件",
        PermissionMode.FullAccess => "所有工具直接执行，请只在信任的任务中使用",
        _ => "工作区可读写；执行命令、越界访问需要确认",
    };
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

    public List<string> DisabledTools { get; set; } = [];

    public string Mode { get; set; } = "office";

    [JsonIgnore]
    public AgentProtocol Protocol => ProviderPreset.For(Provider).Protocol;

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
        DisabledTools = [.. DisabledTools],
        Mode = Mode,
    };
}
