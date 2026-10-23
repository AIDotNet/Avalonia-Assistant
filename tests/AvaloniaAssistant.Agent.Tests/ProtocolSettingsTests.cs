using System.Text.Json;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 协议选择怎么持久化。钉住三件事：旧配置不能把 Anthropic 降级成 Chat、
/// 显式选择要优先于预设、null 不落盘。
/// </summary>
public sealed class ProtocolSettingsTests
{
    [Fact]
    public void PresetsRecommendTheirOwnProtocol()
    {
        Assert.Equal(AgentProtocol.OpenAiCompatible, ProviderPreset.DeepSeek.Protocol);
        Assert.Equal(AgentProtocol.OpenAiCompatible, ProviderPreset.OpenAI.Protocol);
        Assert.Equal(AgentProtocol.AnthropicMessages, ProviderPreset.Anthropic.Protocol);
        Assert.Equal(AgentProtocol.OpenAiCompatible, ProviderPreset.Custom.Protocol);
    }

    [Fact]
    public void ApiIdsMatchTheRegistryKeys()
    {
        Assert.Equal("openai-completions", ProtocolCatalog.ApiId(AgentProtocol.OpenAiCompatible));
        Assert.Equal("openai-responses", ProtocolCatalog.ApiId(AgentProtocol.OpenAiResponses));
        Assert.Equal("anthropic-messages", ProtocolCatalog.ApiId(AgentProtocol.AnthropicMessages));
    }

    [Fact]
    public void LegacySettingsWithoutProtocolFollowThePreset()
    {
        const string json = """
            {
              "provider": "Anthropic",
              "apiKey": "sk-test",
              "baseUrl": "https://api.anthropic.com",
              "model": "claude-sonnet-5-5"
            }
            """;

        var settings = JsonSerializer.Deserialize<AssistantSettings>(json, AssistantJson.Options)!;

        Assert.Null(settings.Protocol);
        Assert.Equal(AgentProtocol.AnthropicMessages, settings.EffectiveProtocol);
    }

    [Fact]
    public void ExplicitProtocolWinsOverThePreset()
    {
        const string json = """
            {
              "provider": "Anthropic",
              "apiKey": "sk-test",
              "baseUrl": "https://api.anthropic.com",
              "model": "claude-sonnet-5-5",
              "protocol": "OpenAiResponses"
            }
            """;

        var settings = JsonSerializer.Deserialize<AssistantSettings>(json, AssistantJson.Options)!;

        Assert.Equal(AgentProtocol.OpenAiResponses, settings.Protocol);
        Assert.Equal(AgentProtocol.OpenAiResponses, settings.EffectiveProtocol);
    }

    [Fact]
    public void NullProtocolIsNotWrittenToDisk()
    {
        var settings = new AssistantSettings { Provider = AgentProviderKind.Anthropic };

        var json = JsonSerializer.Serialize(settings, AssistantJson.Options);

        Assert.DoesNotContain("protocol", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitProtocolRoundTripsAsItsName()
    {
        var settings = new AssistantSettings
        {
            Provider = AgentProviderKind.DeepSeek,
            Protocol = AgentProtocol.OpenAiResponses,
        };

        var json = JsonSerializer.Serialize(settings, AssistantJson.Options);
        var restored = JsonSerializer.Deserialize<AssistantSettings>(json, AssistantJson.Options)!;

        Assert.Contains("\"OpenAiResponses\"", json);
        Assert.Equal(AgentProtocol.OpenAiResponses, restored.Protocol);
        Assert.Equal(AgentProtocol.OpenAiResponses, restored.EffectiveProtocol);
    }

    [Fact]
    public void CloneKeepsTheExplicitProtocol()
    {
        var settings = new AssistantSettings { Protocol = AgentProtocol.AnthropicMessages };

        Assert.Equal(AgentProtocol.AnthropicMessages, settings.Clone().Protocol);
    }
}
