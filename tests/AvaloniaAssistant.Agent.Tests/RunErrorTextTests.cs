using AvaloniaAssistant.Abstractions;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 报错分类：界面只靠这一层决定给用户哪个按钮。
/// 401 必须指向设置，429 才是重试；顺序错了用户就会一直点重试。
/// </summary>
public sealed class RunErrorTextTests
{
    [Theory]
    [InlineData("401 Unauthorized")]
    [InlineData("Invalid API key provided")]
    [InlineData("authentication failed")]
    public void AuthFailuresPointAtSettings(string raw)
    {
        var info = RunErrorInfo.Classify(raw);

        Assert.Equal(RunErrorKind.Auth, info.Kind);
        Assert.Equal("去设置", info.ActionLabel);
        Assert.Contains("Key", info.Hint);
    }

    [Fact]
    public void QuotaWinsOverRateLimitWhenBothAppear()
    {
        // OpenAI 的额度不足本身就是 429，不能当成限流让人反复重试
        var info = RunErrorInfo.Classify("429 insufficient_quota: you exceeded your current quota");

        Assert.Equal(RunErrorKind.Quota, info.Kind);
        Assert.Equal("去设置", info.ActionLabel);
    }

    [Fact]
    public void RateLimitSuggestsRetry()
    {
        var info = RunErrorInfo.Classify("429 Too Many Requests");

        Assert.Equal(RunErrorKind.RateLimited, info.Kind);
        Assert.Equal("重试", info.ActionLabel);
    }

    [Theory]
    [InlineData("404 model_not_found")]
    [InlineData("The model `gpt-x` does not exist")]
    public void MissingModelPointsAtSettings(string raw)
    {
        var info = RunErrorInfo.Classify(raw);

        Assert.Equal(RunErrorKind.ModelNotFound, info.Kind);
        Assert.Equal("去设置", info.ActionLabel);
    }

    [Theory]
    [InlineData("No such host is known. (api.example.com:443)")]
    [InlineData("Connection refused (127.0.0.1:11434)")]
    [InlineData("502: upstream connect failed: 由于目标计算机积极拒绝，无法连接。 (os error 10061)")]
    public void NetworkFailuresAreRecognised(string raw)
    {
        Assert.Equal(RunErrorKind.Network, RunErrorInfo.Classify(raw).Kind);
    }

    [Fact]
    public void TimeoutIsNotMistakenForNetwork()
    {
        var info = RunErrorInfo.Classify("The request timed out after 300s");

        Assert.Equal(RunErrorKind.Timeout, info.Kind);
        Assert.Equal("重试", info.ActionLabel);
    }

    [Fact]
    public void ContextOverflowSuggestsANewTask()
    {
        var info = RunErrorInfo.Classify("This model's maximum context length is 128000 tokens");

        Assert.Equal(RunErrorKind.ContextTooLong, info.Kind);
        Assert.Equal("新建任务", info.ActionLabel);
    }

    [Fact]
    public void TruncatedReplyPointsAtMaxTokens()
    {
        var info = RunErrorInfo.Classify("回复达到长度上限，已截断。");

        Assert.Equal(RunErrorKind.LengthLimit, info.Kind);
        Assert.Equal("去设置", info.ActionLabel);
        Assert.Contains("Max tokens", info.Hint);
    }

    [Fact]
    public void UnknownErrorStillGivesAnAction()
    {
        var info = RunErrorInfo.Classify("something exploded");

        Assert.Equal(RunErrorKind.Unknown, info.Kind);
        Assert.Equal("重试", info.ActionLabel);
        Assert.False(string.IsNullOrWhiteSpace(info.Title));
        Assert.False(string.IsNullOrWhiteSpace(info.Hint));
    }

    [Fact]
    public void ConnectionResultSummaryCarriesTheElapsedTime()
    {
        var success = ConnectionTestResult.Success("deepseek-flash", 412);
        Assert.True(success.Ok);
        Assert.Contains("412 ms", success.Summary);

        var failure = ConnectionTestResult.Failure("API Key 没通过", "检查 Key", 88);
        Assert.False(failure.Ok);
        Assert.Contains("API Key 没通过", failure.Summary);
    }
}
