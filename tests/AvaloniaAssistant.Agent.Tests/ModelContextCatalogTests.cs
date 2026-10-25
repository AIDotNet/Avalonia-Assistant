using AvaloniaAssistant.Agent;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 本地模型目录的解析。水位条和自动压缩都靠这个数，解析错了会静默退回 128k，
/// 所以这里把几种真实形状和坏数据都钉一遍。
/// </summary>
public sealed class ModelContextCatalogTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ava-models-" + Guid.NewGuid().ToString("n"));

    public ModelContextCatalogTests() => Directory.CreateDirectory(_root);

    private string Write(string json)
    {
        var path = Path.Combine(_root, $"models-{Guid.NewGuid():n}.json");
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ReadsWindowFromProviderShape()
    {
        var path = Write("""
            {
              "openai": {
                "models": [
                  { "id": "gpt-6.1-sol", "contextWindow": 200000 },
                  { "id": "deepseek-flash", "contextWindow": 131072 }
                ]
              }
            }
            """);

        var windows = ModelContextCatalog.Load(path);

        Assert.Equal(200_000, windows["gpt-6.1-sol"]);
        Assert.Equal(131_072, windows["deepseek-flash"]);
    }

    [Fact]
    public void ReadsNestedProvidersShapeAndIgnoresBrokenEntries()
    {
        var path = Write("""
            {
              "providers": {
                "local": {
                  "models": [
                    { "id": "qwen3.8:27b", "contextWindow": 32768 },
                    { "id": "no-window" },
                    { "id": "zero", "contextWindow": 0 },
                    { "id": "text", "contextWindow": "128k" },
                    { "contextWindow": 4096 }
                  ]
                }
              }
            }
            """);

        var windows = ModelContextCatalog.Load(path);

        Assert.Single(windows);
        Assert.Equal(32_768, windows["qwen3.8:27b"]);
    }

    [Fact]
    public void MissingOrBrokenFileReturnsEmptyInsteadOfThrowing()
    {
        Assert.Empty(ModelContextCatalog.Load(Path.Combine(_root, "not-there.json")));

        var broken = Write("{ this is not json ");
        Assert.Empty(ModelContextCatalog.Load(broken));

        var array = Write("[1,2,3]");
        Assert.Empty(ModelContextCatalog.Load(array));
    }

    [Fact]
    public void FirstWindowWinsWhenTheSameModelAppearsTwice()
    {
        var path = Write("""
            {
              "a": { "models": [{ "id": "shared", "contextWindow": 8000 }] },
              "b": { "models": [{ "id": "shared", "contextWindow": 999999 }] }
            }
            """);

        Assert.Equal(8_000, ModelContextCatalog.Load(path)["shared"]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
