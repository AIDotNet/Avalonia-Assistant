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
        [AgentProviderKind.DeepSeek] = ["deepseek-flash", "deepseek-v4-pro"],
        [AgentProviderKind.OpenAI] = ["gpt-6.1-sol", "gpt-6-astra", "gpt-6-luna"],
        [AgentProviderKind.Anthropic] = ["claude-opus-5-5", "claude-sonnet-5-5", "claude-haiku-5-5", "claude-fable-5-1"],
        [AgentProviderKind.Custom] = ["qwen3.8:27b", "qwen3.8-flash"],
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
                        catalog.TryGetValue(start.ToolName, out var tool)
                            ? tool.Label
                            : CodingSessionFactory.BuiltinLabel(start.ToolName),
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

    /// <summary>
    /// 探一次 /models。只读接口，不产生生成费用；失败时把请求的 URL 和状态码一起带回去，
    /// 因为「401」和「404」对应的修法完全不同（Key 错 vs Base URL 多/少了 /v1）。
    /// Anthropic 的模型列表在 /v1/models，且要带 anthropic-version。
    /// </summary>
    public async Task<ConnectionProbe> ProbeAsync(
        AssistantSettings settings,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            return new ConnectionProbe(false, "还没有填 API Key。", []);
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            return new ConnectionProbe(false, "Base URL 是空的。", []);
        }

        var anthropic = settings.Protocol == AgentProtocol.AnthropicMessages;
        var baseUrl = settings.BaseUrl.TrimEnd('/');
        var url = anthropic ? baseUrl + "/v1/models" : baseUrl + "/models";

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (anthropic)
        {
            request.Headers.TryAddWithoutValidation("x-api-key", settings.ApiKey);
            request.Headers.TryAddWithoutValidation("anthropic-version", "2023-06-01");
        }
        else
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                return new ConnectionProbe(
                    false,
                    $"GET {url} 返回 {(int)response.StatusCode}：{Snippet(body)}",
                    []);
            }

            var models = ParseModelIds(body);
            return models.Count > 0
                ? new ConnectionProbe(true, $"连接正常，{models.Count} 个模型可用。", models)
                : new ConnectionProbe(true, $"连接正常（GET {url} 返回 200）。", []);
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ConnectionProbe(false, $"请求超时（15 秒）：GET {url}", []);
        }
        catch (Exception ex)
        {
            return new ConnectionProbe(false, $"{ex.Message}（GET {url}）", []);
        }
    }

    private static IReadOnlyList<string> ParseModelIds(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return [];
            }

            var ids = new List<string>();
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 } value)
                {
                    ids.Add(value);
                }
            }

            return ids;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string Snippet(string body)
    {
        var text = body.ReplaceLineEndings(" ").Trim();
        return text.Length <= 200 ? text : text[..200] + "…";
    }

    public void InvalidateSession(string sessionId)
    {
        lock (_sessions)
        {
            _sessions.Remove(sessionId);
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
            if (_sessions.TryGetValue(request.SessionId, out var cached)
                && cached.Matches(history, settings, cwd, request.Permission))
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
                if (_sessions.TryGetValue(request.SessionId, out var cached)
                    && cached.Matches(history, settings, cwd, request.Permission))
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
                Cwd = cwd,
                Permission = request.Permission,
                Model = settings.Model,
                ApiKey = settings.ApiKey,
                BaseUrl = settings.BaseUrl,
                Protocol = settings.Protocol,
                Temperature = settings.Temperature,
                MaxTokens = settings.MaxTokens,
                TrustWorkspace = settings.TrustWorkspace,
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
            var label = info?.Label ?? CodingSessionFactory.BuiltinLabel(name);
            var mutating = ToolAccess.IsMutating(name, info);

            if (mutating && request.Permission == PermissionMode.ReadOnly)
            {
                return new BeforeToolCallResult(
                    Block: true,
                    Reason: $"当前是只读模式，已阻止「{label}」。需要修改时请把权限切换为「默认权限」或「完全访问」。");
            }

            // 完全访问直接放行：不弹确认、不问原因。
            if (PermissionModeText.SkipsApproval(request.Permission) || !ToolAccess.RequiresApproval(name, info))
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
                    label,
                    FormatArguments(context.Args),
                    $"{label} 会修改本机状态，需要你确认。",
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

    /// <summary>
    /// 判断这次带来的历史，是不是在上一轮记录的历史后面继续长的。
    /// </summary>
    /// <remarks>
    /// 这里不能要求全等：正常追问时，界面送来的历史一定比上一轮多出「上一轮的问 + 答」。
    /// 早先按全等比较，缓存永远不命中，每发一条消息都要重建会话（重跑 ModelRuntime、
    /// 重扫技能、重启 MCP 子进程），而且重建时只灌得进问答文本，上一轮的工具结果全丢。
    /// 反过来，历史比记录的短，说明用户重试、截掉了后面的轮次，必须重建——
    /// 否则旧回复还留在会话里，模型会看到两遍。
    /// </remarks>
    private static bool ContinuesHistory(IReadOnlyList<HistoryTurn> previous, IReadOnlyList<HistoryTurn> current)
    {
        if (current.Count < previous.Count)
        {
            return false;
        }

        for (var index = 0; index < previous.Count; index++)
        {
            if (previous[index].Role != current[index].Role || previous[index].Text != current[index].Text)
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

        /// <summary>上一轮提问之前的界面历史。判断能否复用时要看这次是否在它后面接着长。</summary>
        public IReadOnlyList<HistoryTurn> LastHistory { get; set; } = [];

        /// <summary>会话装配时的工作目录。换了工作区必须重建，否则工具还在旧目录里干活。</summary>
        public required string Cwd { get; init; }

        public required PermissionMode Permission { get; init; }

        public required string Model { get; init; }

        public required string ApiKey { get; init; }

        public required string BaseUrl { get; init; }

        /// <summary>协议由服务商推出，但 BaseUrl / Model 可能刚好不变，单独比一次。</summary>
        public required AgentProtocol Protocol { get; init; }

        /// <summary>采样参数在装配时被 StreamSimple 的闭包捕获，改了必须重建才生效。</summary>
        public required double Temperature { get; init; }

        public required int MaxTokens { get; init; }

        /// <summary>决定项目级 .pi 配置读不读，同样在建会话时定下。</summary>
        public required bool TrustWorkspace { get; init; }

        public required IReadOnlyList<string> DisabledTools { get; init; }

        /// <summary>
        /// 会话复用条件：工作目录一致，模型 / 端点 / 工具开关都没变，这次请求的权限档位和
        /// 建会话时一样，且历史是在上一轮后面接着长的。
        /// 权限变了必须重建——BeforeToolCall 闭包和内置工具集都是建会话时定下的。
        /// 这里比的是 request 里的档位，不是设置里的：钩子当初就是按 request 装的。
        /// </summary>
        public bool Matches(
            IReadOnlyList<HistoryTurn> history,
            AssistantSettings settings,
            string cwd,
            PermissionMode permission)
            => string.Equals(Cwd, cwd, StringComparison.OrdinalIgnoreCase)
               && Permission == permission
               && Model == settings.Model
               && ApiKey == settings.ApiKey
               && BaseUrl == settings.BaseUrl
               && Protocol == settings.Protocol
               && Temperature.Equals(settings.Temperature)
               && MaxTokens == settings.MaxTokens
               && TrustWorkspace == settings.TrustWorkspace
               && DisabledTools.SequenceEqual(settings.DisabledTools)
               && ContinuesHistory(LastHistory, history);
    }
}
