using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>仅用于 --screenshot 的离线示例数据，不写入会话目录。</summary>
internal static class DemoData
{
    /// <summary>跨天示例：验证日期分隔和完整时间，正文刻意写短，一屏放得下。</summary>
    public static ChatSession CreateDateDemoSession(string workspace)
    {
        var now = DateTimeOffset.Now;
        return new ChatSession
        {
            Id = "demo-dates",
            Title = "跨天整理周报",
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now,
            WorkspacePath = workspace,
            Entries =
            [
                new ChatEntry
                {
                    Kind = ChatEntryKind.User,
                    Text = "这份周报先列个提纲。",
                    CreatedAt = now.AddDays(-2).AddMinutes(-6),
                },
                new ChatEntry
                {
                    Kind = ChatEntryKind.Assistant,
                    CreatedAt = now.AddDays(-2).AddMinutes(-5),
                    Model = "deepseek-flash",
                    Text = "提纲列了三段：本周进展、卡点、下周计划。",
                },
                new ChatEntry
                {
                    Kind = ChatEntryKind.User,
                    Text = "把第二段补上数据。",
                    CreatedAt = now.AddDays(-1).AddHours(-2),
                },
                new ChatEntry
                {
                    Kind = ChatEntryKind.Assistant,
                    CreatedAt = now.AddDays(-1).AddHours(-2).AddMinutes(1),
                    Model = "deepseek-flash",
                    Text = "补好了，卡点那段现在有两个具体数字。",
                },
                new ChatEntry
                {
                    Kind = ChatEntryKind.User,
                    Text = "今天再收一下尾。",
                    CreatedAt = now.AddMinutes(-2),
                },
                new ChatEntry
                {
                    Kind = ChatEntryKind.Assistant,
                    CreatedAt = now.AddMinutes(-1),
                    Model = "deepseek-flash",
                    Text = "收完了，全文四段，结尾加了下一步动作。",
                },
            ],
        };
    }

    public static ChatSession CreateSession(string workspace)
    {
        var now = DateTimeOffset.Now;
        return new ChatSession
        {
            // 和侧栏第一条演示任务同一个 id：选中态、等待确认的盾牌都按会话 id 关联
            Id = "demo",
            Title = "整理电脑配置清单",
            CreatedAt = now.AddMinutes(-14),
            UpdatedAt = now.AddMinutes(-1),
            WorkspacePath = workspace,
            Entries =
            [
                new ChatEntry
                {
                    Kind = ChatEntryKind.User,
                    Text = "帮我看下这台电脑的配置，顺便把结果整理成一份 Markdown 放到工作区。",
                    CreatedAt = now.AddMinutes(-13),
                    // 示例引用：气泡里那枚可点标签要看得见，截图才能检查大小/行数排版
                    Attachments =
                    [
                        new ChatAttachment
                        {
                            Id = "demo-att-1",
                            Name = "电脑配置清单.md",
                            Path = "电脑配置清单.md",
                            FullPath = Path.Combine(workspace, "电脑配置清单.md"),
                            Size = 2148,
                            LineCount = 42,
                            Text = "# 电脑配置清单",
                        },
                    ],
                },
                new ChatEntry
                {
                    Kind = ChatEntryKind.Assistant,
                    CreatedAt = now.AddMinutes(-12),
                    Model = "deepseek-flash",
                    InputTokens = 1284,
                    OutputTokens = 213,
                    Reasoning = "先跑一条命令查系统信息，确认 CPU、内存和磁盘；再把结果写成文件，最后用一段话说明写到了哪里。",
                    ToolCalls =
                    [
                        new ToolCallRecord
                        {
                            Id = "demo-call-1",
                            Name = "run_command",
                            Label = "执行命令",
                            Arguments = "{\n  \"command\": \"systeminfo\"\n}",
                            Result = "操作系统：Windows 11 专业版 24H2\n处理器：16 个逻辑核心\n物理内存：32 GB（可用 4.1 GB）\n磁盘：C: 剩余 128 GB / 512 GB",
                            DurationMs = 84,
                        },
                        new ToolCallRecord
                        {
                            Id = "demo-call-2",
                            Name = "write_file",
                            Label = "写入文本文件",
                            Arguments = "{\n  \"path\": \"电脑配置.md\"\n}",
                            Result = "已写入 486 个字符到 D://AvaloniaAssistant//Workspace//电脑配置.md。",
                            DurationMs = 32,
                        },
                    ],
                    Artifacts =
                    [
                        new FileChangeRecord
                        {
                            Id = "demo-artifact-1",
                            Path = "电脑配置.md",
                            FullPath = Path.Combine(workspace, "电脑配置.md"),
                            Kind = FileChangeKind.Created,
                            Tool = "write_file",
                            Size = 486,
                            AddedLines = 8,
                            Diff = """
                                   @@ -0,0 +1,8 @@
                                   +# 电脑配置清单
                                   +
                                   +- 系统：Windows 11 专业版 24H2
                                   +- CPU：16 个逻辑核心
                                   +- 内存：32 GB（可用 4.1 GB）
                                   +- 磁盘：C: 剩余 128 GB / 512 GB
                                   +
                                   +> 由 Avalonia Assistant 于 10-27 生成
                                   """,
                        },
                    ],
                    Text = "已经整理好了，文件写到了 `电脑配置.md`。\n\n**这台机器**\n\n- 系统：Windows 11 专业版 24H2\n- CPU：16 个逻辑核心\n- 内存：32 GB，当前可用 4.1 GB\n- 磁盘：C: 剩余 128 GB / 512 GB\n\n内存占用偏高，如果接下来要跑构建或视频渲染，建议先关掉不用的编辑器窗口。",
                },
                new ChatEntry
                {
                    Kind = ChatEntryKind.User,
                    Text = "顺便把下载目录里超过 100MB 的文件列一下。",
                    CreatedAt = now.AddMinutes(-3),
                },
                new ChatEntry
                {
                    Kind = ChatEntryKind.Assistant,
                    CreatedAt = now.AddMinutes(-2),
                    Model = "deepseek-flash",
                    InputTokens = 1650,
                    OutputTokens = 168,
                    Plan =
                    [
                        new PlanStep { Text = "扫描下载目录，列出超过 100MB 的文件", Status = PlanStepStatus.Done },
                        new PlanStep { Text = "按镜像 / 模型 / 录屏分类", Status = PlanStepStatus.InProgress },
                        new PlanStep { Text = "生成清单并归档", Status = PlanStepStatus.Pending },
                    ],
                    ToolCalls =
                    [
                        new ToolCallRecord
                        {
                            Id = "demo-call-3",
                            Name = "ls",
                            Label = "列出目录",
                            Arguments = "{\n  \"path\": \"C:/Users/demo/Downloads\"\n}",
                            Result = "Windows11.iso  5.2 GB\n模型权重.gguf  1.8 GB\n录屏-1015.mp4  640 MB",
                            DurationMs = 41,
                        },
                    ],
                    Text = "下载目录里超过 100MB 的文件有 3 个：\n\n| 文件 | 大小 | 修改时间 |\n| --- | ---: | --- |\n| `Windows11.iso` | 5.2 GB | 10-18 21:40 |\n| `模型权重.gguf` | 1.8 GB | 10-21 09:12 |\n| `录屏-1015.mp4` | 640 MB | 10-15 16:03 |\n\n要我按类型把它们归档到「镜像 / 模型 / 录屏」三个子目录吗？",
                },
            ],
        };
    }
}
