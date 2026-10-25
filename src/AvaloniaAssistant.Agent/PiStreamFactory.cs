using AvaloniaAssistant.Abstractions;
using Pi.Agent;
using Pi.Ai.Api;
using Pi.Ai.Models;
using Pi.Ai.Types;

namespace AvaloniaAssistant.Agent;

/// <summary>把本地设置翻译成 pi.net 的流式函数。测试可替换这一层。</summary>
public interface IPiStreamFactory
{
    StreamFn Create(AssistantSettings settings);
}

public sealed class PiStreamFactory : IPiStreamFactory
{
    public StreamFn Create(AssistantSettings settings)
    {
        return (model, context, _, cancellationToken) =>
        {
            var options = new SimpleStreamOptions(
                ApiKey: settings.ApiKey,
                BaseUrl: string.IsNullOrWhiteSpace(settings.BaseUrl) ? null : settings.BaseUrl,
                MaxTokens: settings.MaxTokens,
                Temperature: settings.Temperature)
            {
                Signal = cancellationToken,
            };

            // 旧入口（Model 重载）返回 Task，Responses 只有同步新入口；统一包成 Task 给 StreamFn。
            Task<IAssistantMessageEventStream> stream = settings.EffectiveProtocol switch
            {
                AgentProtocol.AnthropicMessages => AnthropicMessages.StreamSimple(
                    model, context, options, cancellationToken),
                AgentProtocol.OpenAiResponses => Task.FromResult(OpenAiResponses.StreamSimple(
                    ToSpec(model, settings), context, options, cancellationToken: cancellationToken)),
                _ => OpenAiCompletions.StreamSimple(
                    model, context, options, cancellationToken),
            };

            return stream;
        };
    }

    /// <summary>
    /// Responses 没有接受 <see cref="Model"/> 的旧入口，只能传 <see cref="ModelSpec"/>；
    /// 而且它的 URL 直接拼 <c>model.BaseUrl</c>，不读 options.BaseUrl，所以 BaseUrl 必须落在这里。
    /// </summary>
    private static ModelSpec ToSpec(Model model, AssistantSettings settings) => new()
    {
        Id = model.Id,
        Name = model.Name,
        Api = ProtocolCatalog.ApiId(AgentProtocol.OpenAiResponses),
        Provider = model.Provider,
        BaseUrl = string.IsNullOrWhiteSpace(settings.BaseUrl)
            ? ProviderPreset.OpenAI.BaseUrl
            : settings.BaseUrl,
    };
}
