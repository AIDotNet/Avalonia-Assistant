using System.Text.Json;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 引用资料怎么进入模型请求。这里钉住三件事：没引用就原样发、
/// 引用要带上正文、总量必须封顶。
/// </summary>
public sealed class PromptComposerTests
{
    [Fact]
    public void WithoutAttachmentsThePromptIsUntouched()
    {
        Assert.Equal("帮我看看这个", PromptComposer.Compose("帮我看看这个", []));
        Assert.Equal("原话", PromptComposer.Compose("原话", null));
    }

    [Fact]
    public void AttachmentTextIsWrappedWithItsName()
    {
        var attachment = new ChatAttachment
        {
            Id = "a1",
            Name = "周报.md",
            Kind = "MD",
            Size = 12,
            Text = "本周完成了资料库导入",
        };

        var prompt = PromptComposer.Compose("总结一下", [attachment]);

        Assert.Contains("总结一下", prompt);
        Assert.Contains("<资料 名称=\"周报.md\">", prompt);
        Assert.Contains("本周完成了资料库导入", prompt);
    }

    [Fact]
    public void BinaryAttachmentStatesWhyThereIsNoText()
    {
        var attachment = new ChatAttachment { Name = "diagram.png", Kind = "PNG", Size = 2048 };

        var prompt = PromptComposer.Compose("看一下这张图", [attachment]);

        Assert.Contains("PNG", prompt);
        Assert.Contains("无法读取为文本", prompt);
    }

    [Fact]
    public void TotalSizeIsCappedAcrossAllAttachments()
    {
        var first = new ChatAttachment { Name = "a.md", Kind = "MD", Text = new string('a', 500) };
        var second = new ChatAttachment { Name = "b.md", Kind = "MD", Text = new string('b', 500) };

        var prompt = PromptComposer.Compose("问题", [first, second], maxTotalChars: 120);

        Assert.Contains("已截断", prompt);
        Assert.Contains("未展开", prompt);
        Assert.True(prompt.Length < 700, $"拼接结果应该被限长，实际 {prompt.Length} 字");
    }

    [Fact]
    public void TruncationDoesNotSplitASurrogatePair()
    {
        // emoji 占两个 char，正好卡在边界上时不能把代理对切成半个字符
        var attachment = new ChatAttachment { Name = "emoji.md", Kind = "MD", Text = "ab😀cd" };

        var prompt = PromptComposer.Compose("q", [attachment], maxTotalChars: 3);

        Assert.Contains("已截断", prompt);
        Assert.DoesNotContain('\uFFFD', prompt);
    }

    [Fact]
    public void SessionJsonRoundTripKeepsAttachmentSnapshot()
    {
        var session = new ChatSession
        {
            Entries =
            [
                new ChatEntry
                {
                    Kind = ChatEntryKind.User,
                    Text = "总结 @周报.md",
                    Attachments =
                    [
                        new ChatAttachment
                        {
                            Id = "a1",
                            Name = "周报.md",
                            Kind = "MD",
                            Size = 42,
                            Text = "本周把资料库做完了",
                        },
                    ],
                },
            ],
        };

        var json = JsonSerializer.Serialize(session, AssistantJson.Options);
        var restored = JsonSerializer.Deserialize<ChatSession>(json, AssistantJson.Options);

        var entry = Assert.Single(restored!.Entries);
        Assert.Equal("总结 @周报.md", entry.Text);

        var attachment = Assert.Single(entry.Attachments);
        Assert.Equal("周报.md", attachment.Name);
        Assert.Equal("本周把资料库做完了", attachment.Text);
    }
}
