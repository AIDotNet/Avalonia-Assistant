using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using AvaloniaAssistant.Abstractions;
using Pi.Agent.Types;
using Pi.Ai.Types;
using Pi.CodingAgent.Core;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 用 Pi.CodingAgent 的会话运行时实现 <see cref="IAssistantRuntime"/>。
/// </summary>
/// <remarks>
/// 和上一版（直接用 Pi.Agent 的裸循环）的区别：
/// <list type="bullet">
/// <item>会话是 CodingAgent 的 <see cref="AgentSession"/>，自带技能、MCP、上下文压缩、自动重试；</item>
/// <item>三层权限仍然挂在 <c>Pi.Agent.Agent.BeforeToolCall</c> 上，CodingAgent 自己只把该钩子用于
/// 扩展的 tool_call 事件，这里先串上它再跑审批，扩展行为不受影响；</item>
/// <item>会话存档仍然由 <see cref="ISessionStore"/> 负责，CodingAgent 用内存会话，避免两套 JSON 打架。</item>
/// </list>
/// </remarks>
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
    private readonly IPiStreamFactory? _streams;
    private readonly AgentToolRegistry _registry;
    private readonly Dictionary<string, SessionHolder> _sessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    public PiAgentRuntime(ISettingsProvider settings, IPiStreamFactory? streams = null, AgentToolRegistry? registry = null)
    {
        _settings = settings;
        _streams = streams;
        _registry = registry ?? new AgentToolRegistry();
    }

    public IReadOnlyList<ModelOption> Models => BuildModels(_settings.Current);

    public IReadOnlyList<AgentToolInfo> Tools => _registry.Tools;

    public AgentCapabilities Capabilities => CapabilityScanner.Scan(_settings.Current);

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
        var cwd = ResolveWorkspace(request.WorkspacePath);

        SessionHolder? holder = null;
        string? initError = null;
        try
        {
            holder = await GetOrCreateSessionAsync(request, settings, cwd, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            initError = ex.Message;
        }

        if (holder is null)
        {
            yield return new AgentStreamEvent.RunFailed($"会话初始化失败：{initError}");
            yield break;
        }

        var session = holder.Session;
        var catalog = holder.Catalog;
        using var subscription = session.Agent.Subscribe((@event, _) =>
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

        using var cancellation = cancellationToken.Register(() => _ = session.AbortAsync());

        var run = Task.Run(async () =>
        {
            try
            {
                await writer.WriteAsync(new AgentStreamEvent.RunStarted(request.SessionId, modelOption.Id)).ConfigureAwait(false);
                await session.PromptAsync(request.Prompt, new PromptOptions { Source = "interactive" }).ConfigureAwait(false);

                var last = session.Messages.OfType<AssistantMessage>().LastOrDefault();
                var stats = session.GetSessionStats();
                await writer.WriteAsync(new AgentStreamEvent.RunCompleted(
                    MapStop(last?.StopReason),
                    last?.ErrorMessage,
                    stats.Tokens.Input,
                    stats.Tokens.Output,
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
                holder.LastHistory = request.History;
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
        SessionHolder? holder;
        lock (_sessions)
        {
            _sessions.TryGetValue(sessionId, out holder);
        }

        if (holder is not null)
        {
            _ = holder.Session.AbortAsync();
        }
    }

    public void InvalidateSessions()
    {
        lock (_sessions)
        {
            _sessions.Clear();
        }
    }

    private async Task<SessionHolder> GetOrCreateSessionAsync(
        AgentRunRequest request,
        AssistantSettings settings,
        string cwd,
        CancellationToken cancellationToken)
    {
        var history = request.History;
        lock (_sessions)
        {
            if (_sessions.TryGetValue(request.SessionId, out var cached) && cached.Matches(history, settings))
            {
                return cached;
            }
        }

        await _sessionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_sessions)
            {
                // 双检：并发两轮同时进来时只建一次会话。
                if (_sessions.TryGetValue(request.SessionId, out var cached) && cached.Matches(history, settings))
                {
                    return cached;
                }
            }

            var tools = _registry.CreateCustomTools(request, settings.DisabledTools, out var catalog);
            var session = await CodingSessionFactory
                .CreateAsync(settings, cwd, tools, _streams?.Create(settings), cancellationToken)
                .ConfigureAwait(false);

            InstallPermissionHook(session, request, catalog);
            SeedHistory(session, history);

            var holder = new SessionHolder
            {
                Session = session,
                Catalog = catalog,
                LastHistory = history,
                Permission = request.Permission,
                Model = settings.Model,
                ApiKey = settings.ApiKey,
                BaseUrl = settings.BaseUrl,
                DisabledTools = [.. settings.DisabledTools],
            };

            lock (_sessions)
            {
                _sessions[request.SessionId] = holder;
            }

            return holder;
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>
    /// 三层权限。CodingAgent 在构造时已经把这个钩子指向扩展的 tool_call 事件，这里先调用原钩子，
    /// 再叠加本项目的只读拦截与逐次授权。
    /// </summary>
    private static void InstallPermissionHook(
        AgentSession session,
        AgentRunRequest request,
        IReadOnlyDictionary<string, AgentToolInfo> catalog)
    {
        var previous = session.Agent.BeforeToolCall;
        session.Agent.BeforeToolCall = async (context, token) =>
        {
            if (previous is not null)
            {
                var prior = await previous(context, token).ConfigureAwait(false);
                if (prior?.Block == true)
                {
                    return prior;
                }
            }

            var name = context.ToolCall.Name;
            var info = catalog.TryGetValue(name, out var known) ? known : null;
            var mutating = ToolAccess.IsMutating(name, info);

            if (mutating && request.Permission == PermissionMode.ReadOnly)
            {
                return new BeforeToolCallResult(
                    Block: true,
                    Reason: $"当前是只读模式，已阻止「{info?.Label ?? name}」。需要修改时请把权限切换为「工作区」或「完全访问」。");
            }

            if (!ToolAccess.RequiresApproval(name, info) || request.Permission == PermissionMode.FullAccess)
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
                    name,
                    info?.Label ?? name,
                    FormatArguments(context.Args),
                    $"{info?.Label ?? name} 会修改本机状态，需要你确认。",
                    request.Permission),
                token).ConfigureAwait(false);

            return approved
                ? null
                : new BeforeToolCallResult(Block: true, Reason: "用户拒绝了该操作。请停下来说明原因，不要重复尝试。");
        };
    }

    /// <summary>
    /// 把界面侧的历史灌进新会话。CodingAgent 的 system prompt 以 sections 形式单独维护，
    /// 这里补一条空 system 占位，保证后续差异更新落在头部。
    /// </summary>
    private static void SeedHistory(AgentSession session, IReadOnlyList<HistoryTurn> history)
    {
        var messages = new List<ChatMessage>();
        foreach (var turn in history)
        {
            if (string.IsNullOrWhiteSpace(turn.Text))
            {
                continue;
            }

            messages.Add(turn.Role == "assistant"
                ? Messages.AssistantText(turn.Text)
                : Messages.UserText(turn.Text));
        }

        if (messages.Count == 0)
        {
            return;
        }

        var system = session.Agent.Messages.OfType<SystemMessage>().FirstOrDefault();
        messages.Insert(0, system ?? new SystemMessage(MessageContent.From(string.Empty)));
        session.Agent.Messages = messages;
    }

    private static bool SameHistory(IReadOnlyList<HistoryTurn> left, IReadOnlyList<HistoryTurn> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Count; index++)
        {
            if (left[index].Role != right[index].Role || left[index].Text != right[index].Text)
            {
                return false;
            }
        }

        return true;
    }

    private static string ResolveWorkspace(string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            return Path.GetFullPath(workspacePath);
        }

        var fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Directory.Exists(fallback) ? fallback : Environment.CurrentDirectory;
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

    private sealed class SessionHolder
    {
        public required AgentSession Session { get; init; }

        public required IReadOnlyDictionary<string, AgentToolInfo> Catalog { get; init; }

        public IReadOnlyList<HistoryTurn> LastHistory { get; set; } = [];

        public required PermissionMode Permission { get; init; }

        public required string Model { get; init; }

        public required string ApiKey { get; init; }

        public required string BaseUrl { get; init; }

        public required IReadOnlyList<string> DisabledTools { get; init; }

        /// <summary>
        /// 会话复用条件：历史一致，且权限 / 模型 / 端点 / 工具开关都没变。
        /// 权限变了必须重建——BeforeToolCall 闭包和内置工具集都是建会话时定下的。
        /// </summary>
        public bool Matches(IReadOnlyList<HistoryTurn> history, AssistantSettings settings)
            => Permission == settings.Permission
               && Model == settings.Model
               && ApiKey == settings.ApiKey
               && BaseUrl == settings.BaseUrl
               && DisabledTools.SequenceEqual(settings.DisabledTools)
               && SameHistory(LastHistory, history);
    }
}
