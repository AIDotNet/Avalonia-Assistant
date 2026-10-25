using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>侧栏搜索：除标题外还要能按正文和工具结果命中，并给出一句上下文。</summary>
public sealed class SessionSearchTests
{
    private static ChatSession Session(string title, params ChatEntry[] entries) => new()
    {
        Id = "s1",
        Title = title,
        Entries = [.. entries],
    };

    [Fact]
    public void EmptyQueryMatchesEverythingWithoutSnippet()
    {
        var session = Session("修复 502");

        Assert.True(SessionSearch.TryMatch(session, "  ", out var snippet));
        Assert.Equal(string.Empty, snippet);
    }

    [Fact]
    public void TitleHitMatchesWithoutSnippet()
    {
        var session = Session("注册表代理排查");

        Assert.True(SessionSearch.TryMatch(session, "代理", out var snippet));
        Assert.Equal(string.Empty, snippet);
    }

    [Fact]
    public void BodyHitReturnsSnippetAroundKeyword()
    {
        var session = Session(
            "修复 502",
            new ChatEntry
            {
                Kind = ChatEntryKind.User,
                Text = "帮我看看为什么走代理之后本地模型连不上。",
            });

        Assert.True(SessionSearch.TryMatch(session, "代理", out var snippet));
        Assert.Contains("代理", snippet);
        Assert.Contains("本地模型", snippet);
    }

    [Fact]
    public void ToolResultHitIsSearchable()
    {
        var session = Session(
            "修复 502",
            new ChatEntry
            {
                Kind = ChatEntryKind.Assistant,
                ToolCalls =
                [
                    new ToolCallRecord
                    {
                        Id = "call-1",
                        Name = "run_command",
                        Label = "执行命令",
                        Result = "ProxyEnable 为 0，127.0.0.1:7897 没有监听",
                    },
                ],
            });

        Assert.True(SessionSearch.TryMatch(session, "ProxyEnable", out var snippet));
        Assert.Contains("ProxyEnable", snippet);
    }

    [Fact]
    public void MissingKeywordDoesNotMatch()
    {
        var session = Session(
            "整理下载目录",
            new ChatEntry { Kind = ChatEntryKind.Assistant, Text = "超过 100MB 的有 3 个。" });

        Assert.False(SessionSearch.TryMatch(session, "注册表", out var snippet));
        Assert.Equal(string.Empty, snippet);
    }

    [Fact]
    public void WorkspaceNameIsSearchable()
    {
        var session = Session("修复 502");
        session.WorkspacePath = @"D:\work\Avalonia-Assistant";

        Assert.True(SessionSearch.TryMatch(session, "Avalonia-Assistant", out _));
    }
}
