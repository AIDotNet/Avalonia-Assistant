using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Agent;
using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 多模型配置。钉住三件事：旧配置会被搬成一套模型、列表只来自配置、切换模型等于换整套渠道。
/// </summary>
public sealed class ModelProfileTests
{
    [Fact]
    public void LegacySettingsBecomeOneProfile()
    {
        const string json = """
            {
              "provider": "OpenAI",
              "apiKey": "sk-company",
              "baseUrl": "https://ipsapro.isoftstone.com/thor/v1",
              "model": "deepseek-flash"
            }
            """;

        var settings = JsonSettingsProvider.LoadFromJson(json);

        var profile = Assert.Single(settings.Models);
        Assert.Equal("deepseek-flash", profile.Model);
        Assert.Equal("sk-company", profile.ApiKey);
        Assert.Equal("https://ipsapro.isoftstone.com/thor/v1", profile.BaseUrl);
        Assert.Equal(profile.Id, settings.ActiveModelId);

        // 地址不是 OpenAI 官方端点：实际连的是公司网关，界面不能再写成「OpenAI」
        Assert.Equal(AgentProviderKind.Custom, profile.Provider);
        Assert.Equal(AgentProtocol.OpenAiCompatible, profile.EffectiveProtocol);
    }

    [Fact]
    public void LegacyAnthropicProxyKeepsMessagesProtocol()
    {
        // 走代理的 Claude：地址不是官方的，但协议必须还是 Messages，不能降成 Chat
        const string json = """
            {
              "provider": "Anthropic",
              "apiKey": "sk-proxy",
              "baseUrl": "https://gateway.example.com/anthropic",
              "model": "claude-sonnet-5-5"
            }
            """;

        var profile = Assert.Single(JsonSettingsProvider.LoadFromJson(json).Models);

        Assert.Equal(AgentProviderKind.Custom, profile.Provider);
        Assert.Equal(AgentProtocol.AnthropicMessages, profile.EffectiveProtocol);
    }

    [Fact]
    public async Task SeveralModelsRoundTripWithTheirOwnChannels()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ava-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "settings.json");

        try
        {
            var provider = new JsonSettingsProvider(file);
            await provider.LoadAsync();

            var settings = provider.Current;
            settings.Models.Clear();
            settings.Models.Add(new ModelProfile
            {
                Id = "m-local",
                Name = "公司网关",
                Provider = AgentProviderKind.Custom,
                BaseUrl = "https://ipsapro.isoftstone.com/thor/v1",
                ApiKey = "sk-company",
                Model = "deepseek-flash",
            });
            settings.Models.Add(new ModelProfile
            {
                Id = "m-claude",
                Provider = AgentProviderKind.Anthropic,
                BaseUrl = "https://api.anthropic.com",
                ApiKey = "sk-anthropic",
                Model = "claude-sonnet-5-5",
                Protocol = AgentProtocol.AnthropicMessages,
                ContextWindow = 200_000,
            });
            settings.ActiveModelId = "m-local";

            await provider.SaveAsync(settings);

            var reloaded = new JsonSettingsProvider(file);
            await reloaded.LoadAsync();

            Assert.Equal(2, reloaded.Current.Models.Count);
            Assert.Equal("公司网关", reloaded.Current.Models[0].DisplayName);
            Assert.Equal(200_000, reloaded.Current.Models[1].ContextWindow);

            // 切模型＝换整套渠道：地址、Key、协议一起跟着走
            Assert.True(reloaded.Current.ActivateModel("m-claude"));
            Assert.Equal("sk-anthropic", reloaded.Current.ApiKey);
            Assert.Equal("https://api.anthropic.com", reloaded.Current.BaseUrl);
            Assert.Equal(AgentProtocol.AnthropicMessages, reloaded.Current.EffectiveProtocol);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void NewFormatDoesNotWriteFlattenedModelFields()
    {
        var settings = new AssistantSettings();
        settings.NormalizeModels();

        var root = JsonNode.Parse(JsonSerializer.Serialize(settings, AssistantJson.Options))!.AsObject();

        Assert.True(root.ContainsKey("models"));
        Assert.False(root.ContainsKey("model"));
        Assert.False(root.ContainsKey("apiKey"));
        Assert.False(root.ContainsKey("provider"));
    }

    [Fact]
    public void RuntimeListsOnlyConfiguredModels()
    {
        // 只配了一个 deepseek-flash：列表就该只有它，不能把预设里的 GPT 模型摆出来
        var settings = new AssistantSettings();
        settings.Models.Clear();
        settings.Models.Add(new ModelProfile
        {
            Id = "m-flash",
            Provider = AgentProviderKind.Custom,
            BaseUrl = "https://ipsapro.isoftstone.com/thor/v1",
            ApiKey = "sk-company",
            Model = "deepseek-flash",
        });
        settings.ActiveModelId = "m-flash";

        var runtime = new PiAgentRuntime(new StaticSettings(settings));
        var model = Assert.Single(runtime.Models);

        Assert.Equal("m-flash", model.Id);
        Assert.Equal("deepseek-flash", model.Name);
        Assert.Equal("自定义", model.ProviderName);
        Assert.DoesNotContain(runtime.Models, item => item.Name.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task LegacyFileIsRewrittenInTheNewShape()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ava-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "settings.json");

        try
        {
            await File.WriteAllTextAsync(file, """
                {
                  "provider": "OpenAI",
                  "apiKey": "sk-company",
                  "baseUrl": "https://gw.example.com/v1",
                  "model": "deepseek-flash",
                  "permission": "FullAccess"
                }
                """);

            var provider = new JsonSettingsProvider(file);
            await provider.LoadAsync();
            await provider.SaveAsync(provider.Current);

            var root = JsonNode.Parse(await File.ReadAllTextAsync(file))!.AsObject();

            // 旧字段搬进 models 之后就该消失；否则下次改一处、两处状态就开始打架
            Assert.True(root.ContainsKey("models"));
            Assert.False(root.ContainsKey("provider"));
            Assert.False(root.ContainsKey("apiKey"));
            Assert.False(root.ContainsKey("model"));

            Assert.Equal("sk-company", provider.Current.ApiKey);
            Assert.Equal("deepseek-flash", provider.Current.Model);
            Assert.Equal(PermissionMode.FullAccess, provider.Current.Permission);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class StaticSettings(AssistantSettings settings) : ISettingsProvider
    {
        public AssistantSettings Current => settings;

        public event EventHandler? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AssistantSettings value, CancellationToken cancellationToken = default)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
