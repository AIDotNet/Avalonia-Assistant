using AvaloniaAssistant.Abstractions;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>会话导出：Markdown 要能直接粘进文档，工具与失败轮次要保留上下文。</summary>
public sealed class SessionExportTests
{
    private static ChatSession Sample()
    {
        var session = new ChatSession
        {
            Id = "s1",
            Title = "整理下载目录",
            WorkspacePath = "D:/work",
        };

        session.Entries.Add(new ChatEntry
        {
            Kind = ChatEntryKind.User,
            Text = "把下载目录里超过 100MB 的文件列出来",
            Attachments = [new ChatAttachment { Name = "说明.md", Path = "说明.md" }],
        });

        session.Entries.Add(new ChatEntry
        {
            Kind = ChatEntryKind.Assistant,
            Model = "deepseek-flash",
            Text = "超过 100MB 的有 3 个。",
            InputTokens = 1200,
            OutputTokens = 80,
            ToolCalls =
            [
                new ToolCallRecord
                {
                    Label = "列出目录",
                    Arguments = """{"path":"C:/Users/demo/Downloads"}""",
                    DurationMs = 41,
                },
            ],
        });

        session.Entries.Add(new ChatEntry
        {
            Kind = ChatEntryKind.Assistant,
            Text = string.Empty,
            Error = "401 Unauthorized\nstack...",
        });

        session.Entries.Add(new ChatEntry
        {
            Kind = ChatEntryKind.Notice,
            Text = "已切换到只读权限",
        });

        return session;
    }

    [Fact]
    public void ExportsHeadingAndTurns()
    {
        var markdown = SessionExport.ToMarkdown(Sample());

        Assert.StartsWith("# 整理下载目录", markdown);
        Assert.Contains("1 轮", markdown);
        Assert.Contains("## 你", markdown);
        Assert.Contains("## 助手（deepseek-flash）", markdown);
        Assert.Contains("引用：说明.md", markdown);
    }

    [Fact]
    public void KeepsToolAndFailureContext()
    {
        var markdown = SessionExport.ToMarkdown(Sample());

        Assert.Contains("工具调用：", markdown);
        Assert.Contains("列出目录", markdown);
        Assert.Contains("41 ms", markdown);
        Assert.Contains("这一轮出错了：401 Unauthorized", markdown);
        Assert.Contains("> 已切换到只读权限", markdown);
    }

    [Fact]
    public void IncludesPlanAndArtifacts()
    {
        var session = new ChatSession
        {
            Title = "整理下载目录",
            Entries =
            [
                new ChatEntry { Kind = ChatEntryKind.User, Text = "整理一下" },
                new ChatEntry
                {
                    Kind = ChatEntryKind.Assistant,
                    Text = "整理好了。",
                    Plan =
                    [
                        new PlanStep { Text = "扫描目录", Status = PlanStepStatus.Done },
                        new PlanStep { Text = "按类型归档", Status = PlanStepStatus.InProgress },
                    ],
                    Artifacts =
                    [
                        new FileChangeRecord
                        {
                            Path = "清单.md",
                            FullPath = "D:/w/清单.md",
                            Kind = FileChangeKind.Created,
                            AddedLines = 12,
                        },
                        new FileChangeRecord
                        {
                            Path = "旧.md",
                            FullPath = "D:/w/旧.md",
                            Kind = FileChangeKind.Deleted,
                        },
                    ],
                },
            ],
        };

        var markdown = SessionExport.ToMarkdown(session);

        Assert.Contains("计划：", markdown);
        Assert.Contains("- [x] 扫描目录", markdown);
        Assert.Contains("- [~] 按类型归档", markdown);

        // 导出是为了留档，产物和它加了多少行都得在里面
        Assert.Contains("产物：", markdown);
        Assert.Contains("`清单.md`（新建，+12）", markdown);
        Assert.Contains("`旧.md`（删除）", markdown);
    }

    [Fact]
    public void FileNameStripsInvalidCharacters()
    {
        var name = SessionExport.SuggestFileName("a/b:c*?\"<>|d");

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain(':', name);
        Assert.DoesNotContain('*', name);
        Assert.DoesNotContain('?', name);
        Assert.DoesNotContain('"', name);
        Assert.DoesNotContain('<', name);
        Assert.DoesNotContain('>', name);
        Assert.DoesNotContain('|', name);
    }

    [Fact]
    public void EmptyTitleFallsBack()
    {
        Assert.Equal("会话", SessionExport.SuggestFileName("   "));
    }
}
