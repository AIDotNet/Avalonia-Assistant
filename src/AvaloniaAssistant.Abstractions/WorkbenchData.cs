namespace AvaloniaAssistant.Abstractions;

/// <summary>定时任务的执行频率。间隔（每 N 分钟）暂不提供，避免无人值守下把机器跑满。</summary>
public enum ScheduleFrequency
{
    Once,
    Daily,
    Weekly,
    Monthly,
}

public static class ScheduleFrequencyText
{
    public static string Describe(ScheduleFrequency frequency) => frequency switch
    {
        ScheduleFrequency.Once => "单次",
        ScheduleFrequency.Weekly => "每周",
        ScheduleFrequency.Monthly => "每月",
        _ => "每天",
    };
}

/// <summary>
/// 一条本地定时任务。到达时间后由应用自己以当前登录身份发起一次新任务，
/// 无人值守，所以默认权限下遇到需要确认的工具会停在等待确认（和 WorkBuddy 一致）。
/// </summary>
public sealed class ScheduledTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Name { get; set; } = string.Empty;

    public string Prompt { get; set; } = string.Empty;

    /// <summary>任务执行目录；为空则用所选项目 / 当前工作区。</summary>
    public string? WorkspacePath { get; set; }

    /// <summary>所属项目 id（WorkBuddy 的「项目」来源）。为空表示本地任务。</summary>
    public string? ProjectId { get; set; }

    /// <summary>本任务专用模型配置 id；为空则沿用当前模型。</summary>
    public string? ModelProfileId { get; set; }

    /// <summary>本任务专用专家 id；为空则沿用当前启用的专家。</summary>
    public string? ExpertId { get; set; }

    /// <summary>本任务专用权限档位：default / readonly / full。default=沿用当前设置。</summary>
    public string Permission { get; set; } = TaskPermissionText.Default;

    /// <summary>结果推送：none / system / file。本地助理没有企业微信通道。</summary>
    public string NotifyChannel { get; set; } = TaskNotificationText.System;

    public ScheduleFrequency Frequency { get; set; } = ScheduleFrequency.Daily;

    /// <summary>0–23。</summary>
    public int Hour { get; set; } = 9;

    /// <summary>0–59。</summary>
    public int Minute { get; set; }

    /// <summary>每周：0=周日 … 6=周六。空表示按创建那天。</summary>
    public List<int> Weekdays { get; set; } = [];

    /// <summary>每月：1–31。空表示按创建那天。</summary>
    public List<int> MonthDays { get; set; } = [];

    /// <summary>单次任务的执行日期 yyyy-MM-dd。</summary>
    public string? OnceDate { get; set; }

    /// <summary>有效期 yyyy-MM-dd，留空表示长期有效。</summary>
    public string? ValidFrom { get; set; }

    public string? ValidTo { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset? LastRunAt { get; set; }

    public string? LastResult { get; set; }

    public string? LastSessionId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>一次定时触发的记录。运行记录页列的就是它。</summary>
public sealed class ScheduledRunRecord
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string TaskId { get; set; } = string.Empty;

    public string TaskName { get; set; } = string.Empty;

    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;

    public string Status { get; set; } = "已触发";

    public string? Detail { get; set; }

    public string? SessionId { get; set; }
}

/// <summary>定时任务权限档位的字符串常量（避免存档里存可空枚举）。</summary>
public static class TaskPermissionText
{
    public const string Default = "default";
    public const string ReadOnly = "readonly";
    public const string FullAccess = "full";

    public static string Describe(string? value) => value switch
    {
        ReadOnly => "只读",
        FullAccess => "完全访问",
        _ => "沿用当前档位",
    };

    /// <summary>default 返回 null，表示不要覆盖全局设置。</summary>
    public static PermissionMode? Resolve(string? value) => value switch
    {
        ReadOnly => PermissionMode.ReadOnly,
        FullAccess => PermissionMode.FullAccess,
        _ => null,
    };
}

/// <summary>结果推送渠道。云端 / 企业微信通道不在本地助理范围内。</summary>
public static class TaskNotificationText
{
    public const string None = "none";
    public const string System = "system";
    public const string File = "file";

    public static string Describe(string? value) => value switch
    {
        None => "不推送",
        File => "写入结果文件",
        _ => "系统提醒",
    };
}

/// <summary>项目：把任务、资料和工作目录归到一个持续性的事项下（WorkBuddy 的「项目」）。</summary>
public sealed class Project
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>项目的工作目录；新建任务时以它为工作区。</summary>
    public string? WorkspacePath { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 一条定时任务真正开跑时带上的配置。由存档里的 id 解析成具体值，
/// 交给 ChatViewModel / PiAgentRuntime 落实，不是只存在表单里的摆设。
/// </summary>
public sealed class TaskRunConfig
{
    public string? ModelProfileId { get; init; }

    public string? ExpertDirective { get; init; }

    public PermissionMode? Permission { get; init; }

    public string? WorkspacePath { get; init; }

    public string NotifyChannel { get; init; } = TaskNotificationText.System;

    public string? ProjectId { get; init; }

    public string? ProjectName { get; init; }

    public static TaskRunConfig From(ScheduledTask task, WorkbenchData data)
    {
        var project = task.ProjectId is null
            ? null
            : data.Projects.FirstOrDefault(item => item.Id == task.ProjectId);
        var expert = task.ExpertId is null
            ? null
            : data.Experts.FirstOrDefault(item => item.Id == task.ExpertId && item.Enabled);

        return new TaskRunConfig
        {
            ModelProfileId = string.IsNullOrWhiteSpace(task.ModelProfileId) ? null : task.ModelProfileId,
            ExpertDirective = expert?.Directive,
            Permission = TaskPermissionText.Resolve(task.Permission),
            WorkspacePath = string.IsNullOrWhiteSpace(task.WorkspacePath)
                ? project?.WorkspacePath
                : task.WorkspacePath,
            NotifyChannel = task.NotifyChannel,
            ProjectId = project?.Id,
            ProjectName = project?.Name,
        };
    }
}

/// <summary>资料库里的一条：本机文件或文件夹的绝对路径。</summary>
public sealed class LibraryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Path { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsDirectory { get; set; }

    public string? Note { get; set; }

    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;
}

/// <summary>
/// 专家 = 一套会注入本轮提问的角色指令（WorkBuddy 的「专家」）。
/// 内置档位只读，用户自己加的可以改、可以删。
/// </summary>
public sealed class ExpertPreset
{
    public string Id { get; set; } = Guid.NewGuid().ToString("n");

    public string Name { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Directive { get; set; } = string.Empty;

    public bool BuiltIn { get; set; }

    public bool Enabled { get; set; } = true;
}

/// <summary>内置的几套办公专家。指令会作为 system-reminder 注入本轮提问，不是装饰。</summary>
public static class ExpertCatalog
{
    private static readonly ExpertPreset[] Presets =
    [
        new()
        {
            Id = "builtin-weekly", Name = "周报助手", BuiltIn = true,
            Description = "把零散进展收成三段，语气克制不写空话",
            Directive = "你是务实的周报写手。按「本周进展 / 风险与阻塞 / 下周计划」三段组织，结论先行、不写套话；能落成 docx / xlsx 文件就落成文件。",
        },
        new()
        {
            Id = "builtin-minutes", Name = "会议纪要", BuiltIn = true,
            Description = "议题、结论、待办，待办带负责人和时间",
            Directive = "你是会议纪要整理助手。把零散记录整理成「议题 / 结论 / 待办」三部分，待办必须标负责人和截止时间；没写清的地方标为待确认，不要替人编。",
        },
        new()
        {
            Id = "builtin-data", Name = "数据分析", BuiltIn = true,
            Description = "先看懂表格结构，再给结论和口径",
            Directive = "你是数据分析助手。先说明数据的字段与统计口径，再给结论；涉及合计、均值时要写清按什么维度算，异常值单独列出来。",
        },
        new()
        {
            Id = "builtin-proof", Name = "文档校对", BuiltIn = true,
            Description = "只改错别字和病句，输出修改清单",
            Directive = "你是中文文档校对助手。只改错别字、标点和明显病句，不改写立场和观点；用一张「原文 → 建议 → 理由」的表给出修改清单，不直接重写全文。",
        },
        new()
        {
            Id = "builtin-deck", Name = "汇报提纲", BuiltIn = true,
            Description = "结论先行，一页一个结论加两条依据",
            Directive = "你是汇报材料助手。结论先行，每页只放一个结论加两条依据；先给大纲再补逐页讲稿，材料不足时明确列出需要补充的信息。",
        },
        new()
        {
            Id = "builtin-research", Name = "深度研究", BuiltIn = true,
            Description = "列检索维度，区分事实与推断，标注出处",
            Directive = "你是研究助手。先列出检索维度，再区分事实、推断和待核实的部分，给每条结论标注来源；不确定的地方明确写出来，不要含糊带过。",
        },
    ];

    public static IReadOnlyList<ExpertPreset> BuiltIn => Presets;

    /// <summary>把内置档位补进存档：老存档缺哪条补哪条，用户删不掉内置档位。</summary>
    public static void EnsureBuiltIns(WorkbenchData data)
    {
        foreach (var preset in Presets)
        {
            if (data.Experts.All(item => item.Id != preset.Id))
            {
                data.Experts.Add(new ExpertPreset
                {
                    Id = preset.Id,
                    Name = preset.Name,
                    Description = preset.Description,
                    Directive = preset.Directive,
                    BuiltIn = true,
                    Enabled = true,
                });
            }
        }
    }
}

/// <summary>
/// 工作台相关配置的单一存档根。所有集合挂在一个类型下，Native AOT 的 source-gen
/// 只需要登记这一个根类型，不必给每个 List&lt;T&gt; 单独加 JsonSerializable。
/// </summary>
public sealed class WorkbenchData
{
    public List<ScheduledTask> ScheduledTasks { get; set; } = [];

    public List<ScheduledRunRecord> RunRecords { get; set; } = [];

    public List<LibraryItem> LibraryItems { get; set; } = [];

    public List<ExpertPreset> Experts { get; set; } = [];

    public List<Project> Projects { get; set; } = [];

    /// <summary>当前启用的专家 id；为空表示不挂专家。</summary>
    public string? ActiveExpertId { get; set; }

    /// <summary>是否注册开机自启（明确的用户开关，默认关）。</summary>
    public bool AutoStart { get; set; }
}
