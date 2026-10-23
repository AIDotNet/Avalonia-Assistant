using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AvaloniaAssistant.Abstractions;
using Pi.Agent;
using Pi.Agent.Types;
using PiAgent = Pi.Agent.Agent;
using Pi.Ai.Types;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 用 pi.net 的 Agent 循环实现 <see cref="IAssistantRuntime"/>。
/// 这里是整个解决方案里唯一认识 Pi.* 类型的地方（除测试外）。
/// </summary>
public sealed class PiAgentRuntime : IAssistantRuntime
{
    private static readonly Dictionary<AgentProviderKind, string[]> KnownModels = new()
    {
        [AgentProviderKind.DeepSeek] = ["deepseek-chat", "deepseek-reasoner"],
        [AgentProviderKind.OpenAI] = ["gpt-4.1", "gpt-4.1-mini", "gpt-4o"],
        [AgentProviderKind.Anthropic] = ["claude-sonnet-4-5", "claude-opus-4-1", "claude-haiku-4-5"],
        [AgentProviderKind.Custom] = [],
    };

    private readonly ISettingsProvider _settings;
    private readonly IPiStreamFactory _streams;
    private readonly AgentToolRegistry _registry;
    private readonly Dictionary<string, PiAgent> _active = new(StringComparer.Ordinal);
    private readonly object _activeGate = new();

    public PiAgentRuntime(
        ISettingsProvider settings,
        IPiStreamFactory? streams = null,
        AgentToolRegistry? registry = null)
    {
        _settings = settings;
        _streams = streams ?? new PiStreamFactory();
        _registry = registry ?? new AgentToolRegistry();
    }

    public IReadOnlyList<ModelOption> Models => BuildModels(_settings.Current);

    public IReadOnlyList<AgentToolInfo> Tools => _registry.Tools;

    public async IAsyncEnumerable<AgentStreamEvent> RunAsync(
        AgentRunRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var settings = _settings.Current;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            yield return new AgentStreamEvent.RunFailed("还没有配置模型服务。打开「设置」填入 API Key 后再试。");
            yield break;
        }

        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            yield return new AgentStreamEvent.RunFailed("还没有选择模型。打开「设置」选择一个模型后再试。");
            yield break;
        }

        var channel = Channel.CreateUnbounded<AgentStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

        var writer = channel.Writer;
        var modelOption = ResolveModel(settings);
        var tools = _registry.CreateTools(request, settings.DisabledTools, out var catalog);
        var model = new Model(
            modelOption.Id,
            modelOption.Name,
            settings.Protocol == AgentProtocol.AnthropicMessages ? "anthropic-messages" : "openai-completions",
            settings.Provider.ToString().ToLowerInvariant());

        var agent = CreateAgent(request, settings, model, tools, catalog, writer);
        lock (_activeGate)
        {
            _active[request.SessionId] = agent;
        }

        using var cancellation = cancellationToken.Register(agent.Abort);
        var run = Task.Run(async () =>
        {
            try
            {
                await writer.WriteAsync(new AgentStreamEvent.RunStarted(request.SessionId, modelOption.Id)).ConfigureAwait(false);
                await agent.Prompt(request.Prompt).ConfigureAwait(false);

                var last = agent.Messages.OfType<AssistantMessage>().LastOrDefault();
                await writer.WriteAsync(new AgentStreamEvent.RunCompleted(
                    MapStop(last?.StopReason),
                    last?.ErrorMessage,
                    last?.UsageStats?.Input ?? 0,
                    last?.UsageStats?.Output ?? 0,
                    modelOption.Id)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                writer.TryWrite(new AgentStreamEvent.RunCompleted(AgentStopReason.Aborted, null, 0, 0, modelOption.Id));
            }
            catch (Exception ex)
            {
                writer.TryWrite(new AgentStreamEvent.RunFailed(ex.Message));
            }
            finally
            {
                lock (_activeGate)
                {
                    _active.Remove(request.SessionId);
                }

                writer.TryComplete();
            }
        }, CancellationToken.None);

        await foreach (var @event in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
        {
            yield return @event;
        }

        await run.ConfigureAwait(false);
    }

    public void Abort(string sessionId)
    {
        PiAgent? agent;
        lock (_activeGate)
        {
            _active.TryGetValue(sessionId, out agent);
        }

        agent?.Abort();
    }

    private PiAgent CreateAgent(
        AgentRunRequest request,
        AssistantSettings settings,
        Model model,
        IReadOnlyList<AgentTool> tools,
        IReadOnlyDictionary<string, AgentToolInfo> catalog,
        ChannelWriter<AgentStreamEvent> writer)
    {
        var messages = new List<ChatMessage>
        {
            new SystemMessage(MessageContent.From(SystemPromptBuilder.Build(request, tools))),
        };

        foreach (var turn in request.History)
        {
            if (string.IsNullOrWhiteSpace(turn.Text))
            {
                continue;
            }

            messages.Add(turn.Role == "assistant"
                ? Messages.AssistantText(turn.Text)
                : Messages.UserText(turn.Text));
        }

        var agent = new PiAgent(new AgentOptions
        {
            Messages = messages,
            Model = model,
            Tools = tools,
            StreamFn = _streams.Create(settings),
            ToolExecution = ToolExecutionMode.Sequential,
            BeforeToolCall = async (context, token) =>
            {
                if (!catalog.TryGetValue(context.ToolCall.Name, out var info))
                {
                    return null;
                }

                if (info.IsMutating && request.Permission == PermissionMode.ReadOnly)
                {
                    return new BeforeToolCallResult(
                        Block: true,
                        Reason: $"当前是只读模式，已阻止「{info.Label}」。需要修改时请把权限切换为「工作区」或「完全访问」。");
                }

                if (!info.RequiresApproval || request.Permission == PermissionMode.FullAccess)
                {
                    return null;
                }

                if (request.ApprovalGate is null)
                {
                    return new BeforeToolCallResult(Block: true, Reason: "没有可用的授权界面，已阻止该操作。");
                }

                var approved = await request.ApprovalGate.RequestAsync(
                    new ToolApprovalRequest(
                        request.SessionId,
                        info.Name,
                        info.Label,
                        FormatArguments(context.Args),
                        $"{info.Label} 会修改本机状态，需要你确认。",
                        request.Permission),
                    token).ConfigureAwait(false);

                return approved
                    ? null
                    : new BeforeToolCallResult(Block: true, Reason: "用户拒绝了该操作。请停下来说明原因，不要重复尝试。");
            },
        });

        var subscription = agent.Subscribe((@event, _) =>
        {
            switch (@event)
            {
                case AgentEvent.MessageUpdate { AssistantMessageEvent: AssistantMessageEvent.TextDelta delta }:
                    writer.TryWrite(new AgentStreamEvent.TextDelta(delta.Delta));
                    break;

                case AgentEvent.MessageUpdate { AssistantMessageEvent: AssistantMessageEvent.ThinkingDelta thinking }:
                    writer.TryWrite(new AgentStreamEvent.ThinkingDelta(thinking.Delta));
                    break;

                case AgentEvent.ToolExecutionStart start:
                    writer.TryWrite(new AgentStreamEvent.ToolStarted(
                        start.ToolCallId,
                        start.ToolName,
                        catalog.TryGetValue(start.ToolName, out var tool) ? tool.Label : start.ToolName,
                        FormatArguments(start.Args)));
                    break;

                case AgentEvent.ToolExecutionEnd end:
                    writer.TryWrite(new AgentStreamEvent.ToolFinished(
                        end.ToolCallId,
                        end.ToolName,
                        ExtractResult(end.Result),
                        end.IsError,
                        end.DurationMs));
                    break;
            }

            return Task.CompletedTask;
        });

        // Agent 没有暴露取消订阅的公共字段；订阅随 agent 生命周期结束，
        // 运行时为每次运行新建 agent，因此不会跨运行泄漏监听器。
        _ = subscription;
        return agent;
    }

    private static IReadOnlyList<ModelOption> BuildModels(AssistantSettings settings)
    {
        var preset = ProviderPreset.For(settings.Provider);
        var models = new List<ModelOption>();
        foreach (var id in KnownModels[settings.Provider])
        {
            models.Add(new ModelOption(id, id, preset.Name, preset.Description));
        }

        if (models.All(option => option.Id != settings.Model))
        {
            models.Insert(0, new ModelOption(settings.Model, settings.Model, preset.Name, "当前配置"));
        }

        return models;
    }

    private static ModelOption ResolveModel(AssistantSettings settings)
    {
        var preset = ProviderPreset.For(settings.Provider);
        return new ModelOption(settings.Model, settings.Model, preset.Name, preset.Description);
    }

    private static AgentStopReason MapStop(StopReason? reason) => reason switch
    {
        StopReason.Aborted => AgentStopReason.Aborted,
        StopReason.Error => AgentStopReason.Error,
        StopReason.Length => AgentStopReason.Length,
        _ => AgentStopReason.Completed,
    };

    private static string FormatArguments(object? args)
    {
        if (args is null)
        {
            return "{}";
        }

        try
        {
            return JsonSerializer.Serialize(args, new JsonSerializerOptions { WriteIndented = true });
        }
        catch
        {
            return args.ToString() ?? "{}";
        }
    }

    private static string? ExtractResult(object? result)
    {
        if (result is AgentToolResult toolResult)
        {
            var text = string.Join(
                "\n",
                toolResult.Content.OfType<TextContent>().Select(content => content.Text));
            return text.Length <= 4000 ? text : text[..4000] + "\n…";
        }

        return result?.ToString();
    }
}
