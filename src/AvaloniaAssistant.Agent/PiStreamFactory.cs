using AvaloniaAssistant.Abstractions;
using Pi.Agent;
using Pi.Ai.Api;
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

            return settings.EffectiveProtocol switch
            {
                AgentProtocol.AnthropicMessages => AnthropicMessages.StreamSimple(
                    model, context, options, cancellationToken: cancellationToken),
                AgentProtocol.OpenAiResponses => OpenAiResponses.StreamSimple(
                    model, context, options, cancellationToken: cancellationToken),
                _ => OpenAiCompletions.StreamSimple(
                    model, context, options, cancellationToken: cancellationToken),
            };
        };
    }
}
