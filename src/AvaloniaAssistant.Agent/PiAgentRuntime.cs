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

    /// <inheritdoc />
    public Task<ConnectionTestResult> TestConnectionAsync(
        AssistantSettings settings,
        CancellationToken cancellationToken = default)
        => ConnectionProbe.TestAsync(settings, cancellationToken);

    /// <inheritdoc />
    public Task<McpProbeResult> ProbeMcpServerAsync(
        string serverName,
        CancellationToken cancellationToken = default)
    {
        var capabilities = CapabilityScanner.Scan(_settings.Current);

        // 同名时项目级优先，和 Pi 加载 .pi 的覆盖关系保持一致
        var server = capabilities.McpServers.FirstOrDefault(item => item.Name == serverName && item.IsProjectScope)
                     ?? capabilities.McpServers.FirstOrDefault(item => item.Name == serverName);

        if (server is null)
        {
            return Task.FromResult(McpProbeResult.Failed("配置里找不到这个服务端，可能刚被删掉"));
        }

        var configPath = server.IsProjectScope
            ? capabilities.McpConfigPath
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".pi",
                "agent",
                "mcp.json");

        return McpProber.ProbeAsync(configPath, serverName, cancellationToken);
    }

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

        // 消息下面那行字写的是模型名，不是模型配置的 id：两套配置可以指向同一个模型。
        var runModel = string.IsNullOrWhiteSpace(settings.Model) ? modelOption.Name : settings.Model;
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

        // 采集必须挂在这里，不能挂到界面侧：事件走无界通道，界面收到「工具开始」时
        // 一个几毫秒就能写完的工具早就结束了，那时候再拍快照拍到的是改之后的内容。
        // 这个回调是工具执行前/后同步触发的，拍到的才是真的「改之前」。
        var artifacts = new ArtifactTracker(request.WorkspacePath);

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
                {
                    var arguments = FormatArguments(start.Args);
                    artifacts.ObserveStarted(start.ToolCallId, start.ToolName, arguments);

                    writer.TryWrite(new AgentStreamEvent.ToolStarted(
                        start.ToolCallId,
                        start.ToolName,
                        catalog.TryGetValue(start.ToolName, out var tool)
                            ? tool.Label
                            : CodingSessionFactory.BuiltinLabel(start.ToolName),
                        arguments));
                    break;
                }

                case AgentEvent.ToolExecutionEnd end:
                    writer.TryWrite(new AgentStreamEvent.ToolFinished(
                        end.ToolCallId,
                        end.ToolName,
                        ExtractResult(end.Result),
                        end.IsError,
                        end.DurationMs));

                    // 工具报错时文件可能只写了一半，采集器会自己跳过
                    if (artifacts.ObserveFinished(end.ToolCallId, end.ToolName, end.IsError) is { } change)
                    {
                        writer.TryWrite(new AgentStreamEvent.FileChanged(change));
                    }

                    break;
            }

            return Task.CompletedTask;
        });

        using var cancellation = cancellationToken.Register(() => _ = session.AbortAsync());

        var run = Task.Run(async () =>
        {
            try
            {
                await writer.WriteAsync(new AgentStreamEvent.RunStarted(request.SessionId, runModel)).ConfigureAwait(false);
                await session.PromptAsync(
                        request.Prompt,
                        new PromptOptions { Source = "interactive", Images = LoadImages(request.Images) })
                    .ConfigureAwait(false);

                var last = session.Messages.OfType<AssistantMessage>().LastOrDefault();
                var stats = session.GetSessionStats();
                await writer.WriteAsync(new AgentStreamEvent.RunCompleted(
                    MapStop(last?.StopReason),
                    last?.ErrorMessage,
                    stats.Tokens.Input,
                    stats.Tokens.Output,
                    runModel)).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                writer.TryWrite(new AgentStreamEvent.RunCompleted(AgentStopReason.Aborted, null, 0, 0, runModel));
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
                ModelProfileId = settings.ActiveModelId ?? string.Empty,
                Model = settings.Model,
                ApiKey = settings.ApiKey,
                BaseUrl = settings.BaseUrl,
                Protocol = settings.EffectiveProtocol,
                Temperature = settings.Temperature,
                MaxTokens = settings.MaxTokens,
                TrustWorkspace = settings.TrustWorkspace,
                DisabledTools = [.. settings.DisabledTools],
                DisabledSkills = [.. settings.DisabledSkills],
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
                    Reason: $"当前是只读模式，已阻止「{label}」。需要修改时请把权限切换为「工作区可写」或「完全访问」。");
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

            var decision = await request.ApprovalGate.RequestAsync(
                new ToolApprovalRequest(
                    request.SessionId,
                    name,
                    label,
                    FormatArguments(context.Args),
                    $"{label} 会修改本机状态，需要你确认。",
                    request.Permission),
                token).ConfigureAwait(false);

            if (decision.Allowed)
            {
                return null;
            }

            // 用户在卡片里改了参数：SDK 不允许改写 context.Args，只能拦下这次调用，
            // 把改后的参数写进理由，让模型在下一步原样重发。措辞要足够硬，否则模型会自由发挥。
            if (!string.IsNullOrWhiteSpace(decision.CorrectedArguments))
            {
                return new BeforeToolCallResult(
                    Block: true,
                    Reason: $"用户没有允许原来的参数，而是把它改成了下面这份。"
                            + $"请立刻用这份参数重新调用 {name} 一次，不要解释、不要改动其它字段：\n"
                            + decision.CorrectedArguments);
            }

            return new BeforeToolCallResult(Block: true, Reason: "用户拒绝了该操作。请停下来说明原因，不要重复尝试。");
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
    /// 不能要求全等：正常追问时，界面送来的历史一定比上一轮多出「上一轮的问 + 答」，
    /// 按全等比较会让缓存永远不命中，每发一条消息都重建会话（重跑 ModelRuntime、重扫技能、
    /// 重启 MCP 子进程），而且重建时只灌得进问答文本，上一轮的工具结果全丢。
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

    /// <summary>没选工作区的任务落在 ~/.avaloniaassistant，不再拿整个用户目录当工作区。</summary>
    private static string ResolveWorkspace(string? workspacePath) => PlainWorkspace.Resolve(workspacePath);

    /// <summary>
    /// 输入框里的模型列表就是用户配的那几套，不再按服务商预设脑补。
    /// 以前这里按 <c>KnownModels[provider]</c> 列一长串：连着公司网关、只配了一个 deepseek-flash，
    /// 却能把 OpenAI 预设里的三个 GPT 模型全摆出来，点了必然报错——那些模型端点根本没有。
    /// </summary>
    private static IReadOnlyList<ModelOption> BuildModels(AssistantSettings settings)
    {
        if (settings.Models.Count == 0)
        {
            return [];
        }

        return settings.Models.Select(CreateOption).ToList();
    }

    /// <summary>带上本地目录里的上下文窗口；模型配置里写了覆盖值就用它，查不到才标成估算。</summary>
    private static ModelOption CreateOption(ModelProfile profile)
    {
        var window = profile.ContextWindow ?? ModelContextCatalog.Find(profile.Model);
        return new ModelOption(
            profile.Id,
            profile.DisplayName,
            profile.ProviderName,
            Describe(profile),
            window ?? ModelContextCatalog.FallbackWindow,
            window is null);
    }

    private static ModelOption ResolveModel(AssistantSettings settings)
        => settings.ActiveModel is { } profile
            ? CreateOption(profile)
            : new ModelOption(string.Empty, string.Empty, string.Empty, string.Empty, ModelContextCatalog.FallbackWindow, true);

    /// <summary>浮层第二行：模型名 / 协议 / 主机。同名时不重复写两遍。</summary>
    private static string Describe(ModelProfile profile)
    {
        var parts = new List<string>(3);
        if (!string.Equals(profile.Model, profile.DisplayName, StringComparison.Ordinal))
        {
            parts.Add(profile.Model);
        }

        parts.Add(profile.EffectiveProtocol switch
        {
            AgentProtocol.AnthropicMessages => "Messages",
            AgentProtocol.OpenAiResponses => "Responses",
            _ => "Chat",
        });

        if (Uri.TryCreate(profile.BaseUrl, UriKind.Absolute, out var uri))
        {
            parts.Add(uri.Host);
        }

        return string.Join(" · ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static AgentStopReason MapStop(StopReason? reason) => reason switch
    {
        StopReason.Aborted => AgentStopReason.Aborted,
        StopReason.Error => AgentStopReason.Error,
        StopReason.Length => AgentStopReason.Length,
        _ => AgentStopReason.Completed,
    };

    /// <summary>
    /// 把磁盘上的图片读成模型要的 base64 内容块。单张读不到就跳过——
    /// 一张坏图不该把整轮对话打死。
    /// </summary>
    private static IReadOnlyList<ImageContent>? LoadImages(IReadOnlyList<ChatImage>? images)
    {
        if (images is not { Count: > 0 })
        {
            return null;
        }

        var contents = new List<ImageContent>(images.Count);
        foreach (var image in images)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(image.Path) || !File.Exists(image.Path))
                {
                    continue;
                }

                contents.Add(new ImageContent(
                    Convert.ToBase64String(File.ReadAllBytes(image.Path)),
                    string.IsNullOrWhiteSpace(image.MimeType) ? "image/png" : image.MimeType));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 读失败只丢这一张
            }
        }

        return contents.Count > 0 ? contents : null;
    }

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

        /// <summary>建会话时用的是哪一套模型配置。两套配置可能模型名、端点都一样，但上下文窗口覆盖不同。</summary>
        public required string ModelProfileId { get; init; }

        public required string Model { get; init; }

        public required string ApiKey { get; init; }

        public required string BaseUrl { get; init; }

        public required AgentProtocol Protocol { get; init; }

        public required IReadOnlyList<string> DisabledTools { get; init; }

        public required IReadOnlyList<string> DisabledSkills { get; init; }

        /// <summary>会话装配时的工作目录。换了工作区必须重建，否则工具还在旧目录里干活。</summary>
        public required string Cwd { get; init; }

        /// <summary>采样参数在装配时被 StreamSimple 的闭包捕获，改了必须重建才生效。</summary>
        public required double Temperature { get; init; }

        public required int MaxTokens { get; init; }

        /// <summary>决定项目级 .pi 配置读不读，同样在建会话时定下。</summary>
        public required bool TrustWorkspace { get; init; }

        /// <summary>
        /// 会话复用条件：工作目录一致，模型档案 / 模型 / 端点 / 协议 / 采样参数 / 工具与技能开关都没变，
        /// 本次请求的权限档位和建会话时一样，且历史是在上一轮后面接着长的。
        /// 权限变了必须重建——BeforeToolCall 闭包和内置工具集都是建会话时定下的；
        /// 协议变了也必须重建——流式函数是建会话时按协议选定的。
        /// </summary>
        public bool Matches(
            IReadOnlyList<HistoryTurn> history,
            AssistantSettings settings,
            string cwd,
            PermissionMode permission)
            => string.Equals(Cwd, cwd, StringComparison.OrdinalIgnoreCase)
               && Permission == permission
               && ModelProfileId == (settings.ActiveModelId ?? string.Empty)
               && Model == settings.Model
               && ApiKey == settings.ApiKey
               && BaseUrl == settings.BaseUrl
               && Protocol == settings.EffectiveProtocol
               && Temperature.Equals(settings.Temperature)
               && MaxTokens == settings.MaxTokens
               && TrustWorkspace == settings.TrustWorkspace
               && DisabledTools.SequenceEqual(settings.DisabledTools)
               && DisabledSkills.SequenceEqual(settings.DisabledSkills)
               && ContinuesHistory(LastHistory, history);
    }
}
