using AvaloniaAssistant.Abstractions;
using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
using Pi.CodingAgent;
using Pi.CodingAgent.Core;
using Pi.CodingAgent.Core.Extensions;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 用 Pi.CodingAgent 装配会话：模型运行时、会话、内置工具、技能、MCP、压缩都在这一步接好。
/// 模型出入仍然走本项目自己的 provider 设置，不读 ~/.pi/agent/auth.json。
/// </summary>
internal sealed class CodingSessionFactory
{
    /// <summary>注册到 Pi 模型目录里的 provider id；和本地设置里的服务商无关。</summary>
    private const string ProviderId = "avalonia-assistant";

    /// <summary>
    /// 会话默认启用的内置工具。read 必须留着：技能的正文要靠它按需加载；
    /// 自建工具里已经没有读文件 / 列目录 / 搜内容了，全靠 read / grep / find / ls 顶上；
    /// edit 是唯一能改单行的工具；write 只在完全访问时启用，用来新建文件。
    /// </summary>
    public static IReadOnlyList<string> BuiltinTools(PermissionMode permission) => permission switch
    {
        PermissionMode.ReadOnly => ["read", "grep", "find", "ls"],
        PermissionMode.FullAccess => ["read", "grep", "find", "ls", "edit", "write"],
        _ => ["read", "grep", "find", "ls", "edit"],
    };

    /// <summary>
    /// 内置工具的中文名。它们不在自建工具的 catalog 里，界面拿不到 Label，
    /// 不补这一层工具卡片就只剩英文名（标题和副标题还是重复的同一个词）。
    /// </summary>
    public static string BuiltinLabel(string name) => name switch
    {
        "read" => "读取文件",
        "grep" => "搜索内容",
        "find" => "查找文件",
        "ls" => "列出目录",
        "edit" => "编辑文件",
        "write" => "写入文件",
        "bash" or "powershell" => "执行命令",
        _ => name,
    };

    public static async Task<AgentSession> CreateAsync(
        AssistantSettings settings,
        string cwd,
        IReadOnlyList<ToolDefinition> customTools,
        StreamFn? streamOverride,
        CancellationToken cancellationToken)
    {
        var api = ProtocolCatalog.ApiId(settings.EffectiveProtocol);
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };

        var runtime = await ModelRuntime.CreateAsync(new CreateModelRuntimeOptions
        {
            ModelsPathDisabled = true,
            AllowModelNetwork = false,
            RefreshOnCreate = false,
            Signal = cancellationToken,
        }).ConfigureAwait(false);

        runtime.RegisterProvider(ProviderId, new ProviderConfigInput
        {
            Name = ProviderPreset.For(settings.Provider).Name,
            BaseUrl = settings.BaseUrl,
            ApiKey = settings.ApiKey,
            Api = api,
            // Sdk 内部会按 provider/id 回查目录，模型必须在这里显式登记，否则报 not registered。
            Models =
            [
                new ProviderChatModelConfig
                {
                    Id = settings.Model,
                    Name = settings.Model,
                    Api = api,
                    BaseUrl = settings.BaseUrl,
                    Input = [Pi.Ai.Models.ModelInput.Text],
                    Cost = new ModelCostRates(0, 0),
                    Reasoning = false,
                    // 目录里查不到该模型的真实窗口，取一个保守值让压缩按比例触发。
                    ContextWindow = 128_000,
                    MaxTokens = settings.MaxTokens,
                },
            ],
            StreamSimple = (spec, context, _) =>
            {
                var options = new SimpleStreamOptions(
                    ApiKey: settings.ApiKey,
                    BaseUrl: string.IsNullOrWhiteSpace(settings.BaseUrl) ? null : settings.BaseUrl,
                    MaxTokens: settings.MaxTokens,
                    Temperature: settings.Temperature);

                // 测试注入的脚本化模型走这里；它的 Task 已经是完成态，不会阻塞。
                if (streamOverride is not null)
                {
                    var model = new Model(spec.Id, spec.Name, spec.Api, spec.Provider);
                    return streamOverride(model, context, options, CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                }

                var streamingModel = spec with { Api = api, BaseUrl = settings.BaseUrl };
                return settings.EffectiveProtocol switch
                {
                    AgentProtocol.AnthropicMessages => AnthropicMessages.StreamSimple(
                        streamingModel, context, options, http, CancellationToken.None),
                    AgentProtocol.OpenAiResponses => OpenAiResponses.StreamSimple(
                        streamingModel, context, options, http, CancellationToken.None),
                    _ => OpenAiCompletions.StreamSimple(
                        streamingModel, context, options, http, CancellationToken.None),
                };
            },
        });

        var agentDir = Config.GetAgentDir();
        var settingsManager = SettingsManager.Create(cwd, agentDir, new SettingsManagerCreateOptions
        {
            // 项目级 mcp.json / AGENTS.md 只在受信任项目里读；信任与否由本地设置决定。
            ProjectTrusted = settings.TrustWorkspace,
        });

        // SDK 不会像 CLI 那样自动装内置扩展：技能靠 loader，MCP / codemode / tool-search 靠这四个扩展工厂。
        var resourceLoader = new DefaultResourceLoader(
            cwd,
            agentDir,
            settingsManager,
            extensionFactories: BuiltInExtensions.All);
        await resourceLoader.ReloadAsync(null, cancellationToken).ConfigureAwait(false);

        var result = await Sdk.CreateAgentSessionAsync(
            new CreateAgentSessionOptions
            {
                Cwd = cwd,
                ModelRuntime = runtime,
                ResourceLoader = resourceLoader,
                SettingsManager = settingsManager,
                Model = new ModelSpec
                {
                    Id = settings.Model,
                    Name = settings.Model,
                    Api = api,
                    Provider = ProviderId,
                    BaseUrl = settings.BaseUrl,
                },
                // 会话只活在进程内：磁盘上的会话存档仍然由 ISessionStore 管，
                // 两套 JSONL/JSON 同时落盘会互相打脸。
                SessionManager = SessionManager.InMemory(cwd),
                // allowlist 同时决定「哪些工具算激活」：自建工具虽然走 CustomTools 注册，
                // 名字也必须出现在这里，否则模型看不到它们。
                // mcp__* 不能省：显式 allowlist 会把 MCP 工具排除在激活集之外，只留给 codemode。
                Tools = [.. BuiltinTools(settings.Permission), .. customTools.Select(tool => tool.Name), "mcp__*"],
                CustomTools = customTools,
            },
            processCwd: cwd,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        // SDK 装配不会像交互模式那样绑定扩展，扩展收不到 session_start，MCP 就永远不连接。
        // 这里补一次绑定：Mode 用 print（不提供 TUI 上下文），扩展照样能加载服务端与资源。
        await result.Session
            .BindExtensionsAsync(new ExtensionBindings { Mode = "print" })
            .ConfigureAwait(false);

        return result.Session;
    }
}
