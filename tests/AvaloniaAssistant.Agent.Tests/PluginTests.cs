using AvaloniaAssistant.Abstractions;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 插件相关的两层验证：
/// <list type="number">
/// <item>源解析 / 扩展入口发现是纯逻辑，直接断言；</item>
/// <item>真正的包（本地目录形态）能不能被 1.4.5 的进程内 JS 宿主加载——这是升级到 1.4.5 的全部理由，
/// 只测「装上了」不算数。</item>
/// </list>
/// </summary>
public sealed class PluginTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "ava-plugin-" + Guid.NewGuid().ToString("n"));

    public PluginTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响断言。
        }
    }

    // ---- 源解析 ----

    [Theory]
    [InlineData("pi-memory", "npm:pi-memory")]
    [InlineData("@scope/pkg", "npm:@scope/pkg")]
    [InlineData("@scope/pkg@1.2.3", "npm:@scope/pkg@1.2.3")]
    [InlineData("npm:pi-web-access@0.46.0", "npm:pi-web-access@0.46.0")]
    [InlineData("git:github.com/a/b@v1", "git:github.com/a/b@v1")]
    [InlineData("github.com/a/b", "github.com/a/b")]
    [InlineData("https://github.com/a/b", "https://github.com/a/b")]
    [InlineData("./local-plugin", "./local-plugin")]
    [InlineData("D:\\work\\plugin", "D:\\work\\plugin")]
    public void NormalizesSourceLikePi(string input, string expected)
        => Assert.Equal(expected, PluginDiscovery.NormalizeSource(input));

    [Theory]
    [InlineData("npm:pi-memory", "pi-memory")]
    [InlineData("npm:@scope/pkg@1.2.3", "@scope/pkg")]
    [InlineData("npm:@scope/pkg", "@scope/pkg")]
    [InlineData("git:github.com/a/b", "github.com/a/b")]
    public void DisplayNameDropsPrefixAndVersion(string source, string expected)
        => Assert.Equal(expected, PluginDiscovery.DisplayName(source));

    [Fact]
    public void BlankSourceStaysBlank()
        => Assert.Equal(string.Empty, PluginDiscovery.NormalizeSource("   "));

    // ---- 扩展入口发现 ----

    [Fact]
    public void FindsExtensionsDeclaredInManifest()
    {
        var package = Path.Combine(_root, "pkg");
        Directory.CreateDirectory(Path.Combine(package, "src"));
        File.WriteAllText(Path.Combine(package, "package.json"), """
            { "name": "demo", "pi": { "extensions": ["./src/main.ts"] } }
            """);
        File.WriteAllText(Path.Combine(package, "src", "main.ts"), "export default function () {}");

        Assert.Equal(["src/main.ts"], PluginDiscovery.FindExtensions(package));
    }

    [Fact]
    public void ExpandsManifestGlobs()
    {
        var package = Path.Combine(_root, "pkg-glob");
        Directory.CreateDirectory(Path.Combine(package, "tools", "a"));
        Directory.CreateDirectory(Path.Combine(package, "tools", "b"));
        File.WriteAllText(Path.Combine(package, "package.json"), """
            { "name": "demo", "pi": { "extensions": ["./tools/*/index.ts"] } }
            """);
        File.WriteAllText(Path.Combine(package, "tools", "a", "index.ts"), "export default function () {}");
        File.WriteAllText(Path.Combine(package, "tools", "b", "index.ts"), "export default function () {}");

        var entries = PluginDiscovery.FindExtensions(package);
        Assert.Equal(2, entries.Count);
        Assert.Contains("tools/a/index.ts", entries);
        Assert.Contains("tools/b/index.ts", entries);
    }

    [Fact]
    public void FallsBackToConventionalExtensionsDirectory()
    {
        var package = Path.Combine(_root, "pkg-conventional");
        Directory.CreateDirectory(Path.Combine(package, "extensions"));
        File.WriteAllText(Path.Combine(package, "extensions", "hello.ts"), "export default function () {}");
        File.WriteAllText(Path.Combine(package, "extensions", "readme.md"), "不是扩展");

        Assert.Equal(["extensions/hello.ts"], PluginDiscovery.FindExtensions(package));
    }

    [Fact]
    public void PackageRootIndexAloneIsNotAnExtension()
    {
        // 上游只对手动放置的扩展目录认 index.ts；包必须有 pi 清单或 extensions/ 目录，
        // 否则界面会报「扩展 1 个」，会话里一个都没加载。
        var package = Path.Combine(_root, "pkg-root");
        Directory.CreateDirectory(package);
        File.WriteAllText(Path.Combine(package, "index.js"), "module.exports = function () {}");

        Assert.Empty(PluginDiscovery.FindExtensions(package));
    }

    [Fact]
    public void ManifestWithoutExtensionsIgnoresConventionalDirectory()
    {
        // 有 pi 清单就只认清单，extensions/ 目录不再自动收——和 ResourceLoader 一致。
        var package = Path.Combine(_root, "pkg-manifest-empty");
        Directory.CreateDirectory(Path.Combine(package, "extensions"));
        File.WriteAllText(Path.Combine(package, "package.json"), """
            { "name": "demo", "pi": { "skills": ["./skills"] } }
            """);
        File.WriteAllText(Path.Combine(package, "extensions", "hello.ts"), "export default function () {}");

        Assert.Empty(PluginDiscovery.FindExtensions(package));
    }

    [Fact]
    public void ManifestDirectoryEntryExpandsItsOwnIndex()
    {
        var package = Path.Combine(_root, "pkg-dir-entry");
        Directory.CreateDirectory(Path.Combine(package, "tools", "review"));
        File.WriteAllText(Path.Combine(package, "package.json"), """
            { "name": "demo", "pi": { "extensions": ["./tools/review"] } }
            """);
        File.WriteAllText(Path.Combine(package, "tools", "review", "index.ts"), "export default function () {}");

        Assert.Equal(["tools/review/index.ts"], PluginDiscovery.FindExtensions(package));
    }

    [Fact]
    public void ManifestKeepsDotPrefixedDirectories()
    {
        // "./" 只去一层；不能把 ./.pi/... 变成 pi/...
        var package = Path.Combine(_root, "pkg-dot");
        Directory.CreateDirectory(Path.Combine(package, ".pi"));
        File.WriteAllText(Path.Combine(package, "package.json"), """
            { "name": "demo", "pi": { "extensions": ["./.pi/main.ts"] } }
            """);
        File.WriteAllText(Path.Combine(package, ".pi", "main.ts"), "export default function () {}");

        Assert.Equal([".pi/main.ts"], PluginDiscovery.FindExtensions(package));
    }

    [Fact]
    public void SkillOnlyPackageReportsNoExtensions()
    {
        var package = Path.Combine(_root, "pkg-skills");
        Directory.CreateDirectory(Path.Combine(package, "skills", "demo"));
        File.WriteAllText(Path.Combine(package, "skills", "demo", "SKILL.md"), "# demo");

        Assert.Empty(PluginDiscovery.FindExtensions(package));
    }

    // ---- 停用形态 ----

    [Fact]
    public void DisabledEntryIsRecognized()
    {
        // 停用 = 四类资源都写成空数组；只关一类不算整体停用
        var disabled = PluginDiscovery.DisabledEntry("npm:pi-memory");
        Assert.True(PluginDiscovery.IsDisabledEntry(disabled));
        Assert.Equal("npm:pi-memory", PluginDiscovery.PackageSourceOf(disabled));

        var onlyExtensionsOff = System.Text.Json.Nodes.JsonNode.Parse("""
            { "source": "npm:x", "extensions": [] }
            """);
        Assert.False(PluginDiscovery.IsDisabledEntry(onlyExtensionsOff));

        Assert.False(PluginDiscovery.IsDisabledEntry(System.Text.Json.Nodes.JsonValue.Create("npm:x")));
        Assert.Equal("npm:x", PluginDiscovery.PackageSourceOf(System.Text.Json.Nodes.JsonValue.Create("npm:x")));
    }

    [Fact]
    public async Task DisabledPackageIsNotLoaded()
    {
        var workspace = Path.Combine(_root, "ws-disabled");
        var package = Path.Combine(_root, "plugin-disabled");
        Directory.CreateDirectory(Path.Combine(workspace, ".pi"));
        Directory.CreateDirectory(package);

        File.WriteAllText(Path.Combine(package, "package.json"), """
            { "name": "disabled-fixture", "pi": { "extensions": ["./index.ts"] } }
            """);
        File.WriteAllText(Path.Combine(package, "index.ts"), """
            export default function (pi: any) {
              pi.registerCommand("disabled-fixture-hello", { description: "x", handler: async () => {} });
            }
            """);

        // 项目级对象条目：source 指向本地包，四类资源全空 = 停用
        File.WriteAllText(
            Path.Combine(workspace, ".pi", "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                packages = new object[]
                {
                    new
                    {
                        source = package.Replace('\\', '/'),
                        extensions = Array.Empty<string>(),
                        skills = Array.Empty<string>(),
                        prompts = Array.Empty<string>(),
                        themes = Array.Empty<string>(),
                    },
                },
            }));

        var session = await CodingSessionFactory.CreateAsync(
            new TestSettings().Current, workspace, [], null, CancellationToken.None);

        var loaded = session.ResourceLoader.GetExtensions();
        Assert.DoesNotContain(
            loaded.Extensions,
            extension => extension.Commands.ContainsKey("disabled-fixture-hello"));
    }

    // ---- 1.4.5：TypeScript 扩展真能加载 ----

    [Fact]
    public async Task LoadsTypeScriptPackageExtension()
    {
        var workspace = Path.Combine(_root, "workspace");
        var package = Path.Combine(_root, "plugin-package");
        Directory.CreateDirectory(Path.Combine(workspace, ".pi"));
        Directory.CreateDirectory(package);

        File.WriteAllText(Path.Combine(package, "package.json"), """
            {
              "name": "ava-plugin-fixture",
              "version": "0.0.1",
              "keywords": ["pi-package"],
              "pi": { "extensions": ["./index.ts"] }
            }
            """);

        // 故意带类型注解：只有真的走 TypeScript 转译才会成功。
        File.WriteAllText(Path.Combine(package, "index.ts"), """
            export default function (pi: any) {
              pi.registerCommand("plugin-hello", {
                description: "来自测试插件",
                handler: async () => {}
              });
            }
            """);

        File.WriteAllText(
            Path.Combine(workspace, ".pi", "settings.json"),
            System.Text.Json.JsonSerializer.Serialize(new
            {
                packages = new[] { package.Replace('\\', '/') },
            }));

        var settings = new TestSettings().Current;
        var session = await CodingSessionFactory.CreateAsync(
            settings, workspace, [], null, CancellationToken.None);

        var loaded = session.ResourceLoader.GetExtensions();
        var extension = loaded.Extensions.FirstOrDefault(
            item => item.ResolvedPath.EndsWith("index.ts", StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(extension);
        Assert.Contains("plugin-hello", extension!.Commands.Keys);
        Assert.DoesNotContain(loaded.Errors, error => error.Path.Contains("index.ts", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class TestSettings : ISettingsProvider
    {
        public AssistantSettings Current { get; } = new()
        {
            Provider = AgentProviderKind.Custom,
            ApiKey = "test-key",
            BaseUrl = "http://localhost",
            Model = "test-model",
            Permission = PermissionMode.Workspace,
            TrustWorkspace = true,
        };

        public event EventHandler? Changed;

        public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default)
        {
            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        }
    }
}
