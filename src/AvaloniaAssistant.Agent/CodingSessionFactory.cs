using AvaloniaAssistant.Abstractions;
using Pi.Agent.Types;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;
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

    /// <summary>会话默认启用的内置工具。read/ls/write/bash 由自建工具承担，避免同一件事两个工具。</summary>
    public static IReadOnlyList<string> BuiltinTools(PermissionMode permission) =>
        permission == PermissionMode.ReadOnly
            ? ["grep", "find"]
            : ["grep", "find", "edit"];

    public static async Task<AgentSession> CreateAsync(
        AssistantSettings settings,
        string cwd,
        IReadOnlyList<ToolDefinition> customTools,
        CancellationToken cancellationToken)
    {
        var api = ApiName(settings);
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
            StreamSimple = (spec, context, _) =>
            {
                var options = new SimpleStreamOptions(
                    ApiKey: settings.ApiKey,
                    BaseUrl: string.IsNullOrWhiteSpace(settings.BaseUrl) ? null : settings.BaseUrl,
                    MaxTokens: settings.MaxTokens,
                    Temperature: settings.Temperature);

                var model = spec with { Api = api, BaseUrl = settings.BaseUrl };
                return settings.Protocol == AgentProtocol.AnthropicMessages
                    ? AnthropicMessages.StreamSimple(model, context, options, http, CancellationToken.None)
                    : OpenAiCompletions.StreamSimple(model, context, options, http, CancellationToken.None);
            },
        });

        var result = await Sdk.CreateAgentSessionAsync(
            new CreateAgentSessionOptions
            {
                Cwd = cwd,
                ModelRuntime = runtime,
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
                Tools = BuiltinTools(settings.Permission),
                CustomTools = customTools,
            },
            processCwd: cwd,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        return result.Session;
    }

    private static string ApiName(AssistantSettings settings)
        => settings.Protocol == AgentProtocol.AnthropicMessages ? "anthropic-messages" : "openai-completions";
}
