using AvaloniaAssistant.Agent;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 装插件时的进程环境。踩过的坑：从别的宿主进程继承来的 127.0.0.1:xxxxx 代理端口，
/// 宿主一退就接不通，npm 会一直重试——界面上就是「正在安装…」永远转。
/// </summary>
public sealed class PackageRunnerTests
{
    [Theory]
    [InlineData("http://127.0.0.1:58647", true)]
    [InlineData("http://localhost:7897", true)]
    [InlineData("http://[::1]:7897", true)]
    [InlineData("http://10.136.2.166:8080", false)]
    [InlineData("https://proxy.corp.example:3128", false)]
    [InlineData("not a url", false)]
    [InlineData("", false)]
    public void OnlyLoopbackProxiesAreDropped(string value, bool expected)
        => Assert.Equal(expected, PiPackageCommandRunner.IsLoopbackProxy(value));

    [Theory]
    [InlineData("install", true)]
    [InlineData("update", true)]
    [InlineData("view", false)]
    public void NpmInstallGetsAuditAndFundOff(string verb, bool expectFlags)
    {
        var args = PiPackageCommandRunner.AdjustArguments("npm", [verb, "pi-web-access"]);

        Assert.Equal(expectFlags, args.Contains("--no-audit"));
        Assert.Equal(expectFlags, args.Contains("--no-fund"));
        Assert.Contains("pi-web-access", args);
    }

    [Fact]
    public void GitArgumentsAreLeftAlone()
    {
        var args = PiPackageCommandRunner.AdjustArguments("git", ["clone", "--depth", "1", "https://github.com/a/b"]);

        Assert.DoesNotContain("--no-audit", args);
    }

    [Fact]
    public void RegistryIsPassedToNpm()
    {
        var env = PiPackageCommandRunner.BuildEnvironment(null, "https://registry.npmmirror.com");

        Assert.Equal("https://registry.npmmirror.com", env["npm_config_registry"]);
        Assert.Equal("2", env["npm_config_fetch_retries"]);
        Assert.Equal("60000", env["npm_config_fetch_timeout"]);
    }

    [Fact]
    public void EmptyRegistryLeavesNpmDefaultAlone()
    {
        var env = PiPackageCommandRunner.BuildEnvironment(null, "   ");

        Assert.False(env.ContainsKey("npm_config_registry"));
    }

    [Fact]
    public void PiOwnEnvironmentWins()
    {
        var env = PiPackageCommandRunner.BuildEnvironment(
            new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["npm_config_registry"] = "http://mirror.local" },
            "https://registry.npmmirror.com");

        Assert.Equal("0", env["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("http://mirror.local", env["npm_config_registry"]);
    }
}
