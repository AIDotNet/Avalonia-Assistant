using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using Xunit;

namespace AvaloniaAssistant.Agent.Tests;

/// <summary>
/// 工作台的真实功能：存档往返、定时任务到点判定、专家指令注入。
/// 这几项都不是界面上的装饰，所以要有能跑的单测。
/// </summary>
public sealed class WorkbenchTests
{
    [Fact]
    public async Task WorkbenchStoreRoundTripKeepsEverything()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ava-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        var store = new JsonWorkbenchStore(directory);
        await store.LoadAsync();
        Assert.Empty(store.Data.ScheduledTasks);

        store.Data.ScheduledTasks.Add(new ScheduledTask
        {
            Name = "每日简报",
            Prompt = "汇总一下今天的文件",
            Frequency = ScheduleFrequency.Daily,
            Hour = 9,
            Minute = 30,
            WorkspacePath = @"D:/work/日报",
        });
        store.Data.LibraryItems.Add(new LibraryItem { Name = "周报.md", Path = @"D:/work/周报.md" });
        store.Data.Experts.Add(new ExpertPreset
        {
            Id = "custom-1", Name = "合同审阅", Directive = "逐条列出风险条款", BuiltIn = false,
        });
        store.Data.ActiveExpertId = "custom-1";
        store.Data.RunRecords.Add(new ScheduledRunRecord { TaskName = "每日简报", Status = "已触发" });
        await store.SaveAsync();

        var reloaded = new JsonWorkbenchStore(directory);
        await reloaded.LoadAsync();

        Assert.Equal("每日简报", Assert.Single(reloaded.Data.ScheduledTasks).Name);
        Assert.Equal(9, reloaded.Data.ScheduledTasks[0].Hour);
        Assert.Equal(30, reloaded.Data.ScheduledTasks[0].Minute);
        Assert.Equal(@"D:/work/日报", reloaded.Data.ScheduledTasks[0].WorkspacePath);
        Assert.Equal(@"D:/work/周报.md", Assert.Single(reloaded.Data.LibraryItems).Path);
        Assert.Equal("逐条列出风险条款", Assert.Single(reloaded.Data.Experts).Directive);
        Assert.Equal("custom-1", reloaded.Data.ActiveExpertId);
        Assert.Equal("已触发", Assert.Single(reloaded.Data.RunRecords).Status);

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task CorruptWorkbenchFileDoesNotBreakLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ava-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "workbench.json"), "{ this is not json");

        var store = new JsonWorkbenchStore(directory);
        await store.LoadAsync();

        Assert.Empty(store.Data.ScheduledTasks);
        Assert.Empty(store.Data.LibraryItems);

        Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public void ExpertCatalogProvidesOfficePresets()
    {
        var data = new WorkbenchData();
        ExpertCatalog.EnsureBuiltIns(data);

        Assert.True(data.Experts.Count >= 6);
        Assert.All(data.Experts, item => Assert.False(string.IsNullOrWhiteSpace(item.Directive)));
        Assert.Contains(data.Experts, item => item.Name == "周报助手");

        // 再补一次不能重复，也不能把用户自己加的删掉
        data.Experts.Add(new ExpertPreset { Id = "mine", Name = "我的", Directive = "x" });
        ExpertCatalog.EnsureBuiltIns(data);
        Assert.Equal(7, data.Experts.Count);
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute)
        => new(new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Local));

    [Fact]
    public void DailyTaskFiresOnlyInItsMinuteWindow()
    {
        var task = new ScheduledTask { Frequency = ScheduleFrequency.Daily, Hour = 9, Minute = 0 };
        var due = At(2026, 11, 16, 9, 0);

        Assert.True(ScheduleMath.IsDueInWindow(task, due.AddSeconds(-30), due));
        Assert.False(ScheduleMath.IsDueInWindow(task, due.AddSeconds(-30), due.AddSeconds(-1)));
        Assert.False(ScheduleMath.IsDueInWindow(task, due.AddSeconds(1), due.AddMinutes(2)));
    }

    [Fact]
    public void WeeklyTaskRespectsSelectedWeekdays()
    {
        // 2026-11-16 是周一（(int)DayOfWeek.Monday == 1）
        var task = new ScheduledTask
        {
            Frequency = ScheduleFrequency.Weekly,
            Hour = 9,
            Minute = 0,
            Weekdays = [1, 3, 5],
        };

        Assert.True(ScheduleMath.MatchesDate(task, new DateOnly(2026, 11, 16)));
        Assert.False(ScheduleMath.MatchesDate(task, new DateOnly(2026, 11, 17)));
        Assert.True(ScheduleMath.MatchesDate(task, new DateOnly(2026, 11, 18)));
    }

    [Fact]
    public void OnceTaskAndValidityRangeAreHonored()
    {
        var once = new ScheduledTask
        {
            Frequency = ScheduleFrequency.Once,
            OnceDate = "2026-11-16",
            Hour = 9,
        };

        Assert.True(ScheduleMath.MatchesDate(once, new DateOnly(2026, 11, 16)));
        Assert.False(ScheduleMath.MatchesDate(once, new DateOnly(2026, 11, 17)));

        var limited = new ScheduledTask
        {
            Frequency = ScheduleFrequency.Daily,
            ValidFrom = "2026-11-10",
            ValidTo = "2026-11-15",
            Hour = 9,
        };
        Assert.False(ScheduleMath.MatchesDate(limited, new DateOnly(2026, 11, 16)));
        Assert.True(ScheduleMath.MatchesDate(limited, new DateOnly(2026, 11, 12)));
        Assert.False(ScheduleMath.MatchesDate(limited, new DateOnly(2026, 11, 9)));
    }

    [Fact]
    public void DisabledAndInvalidTasksNeverFire()
    {
        var disabled = new ScheduledTask { Enabled = false, Hour = 9, Minute = 0 };
        var due = At(2026, 11, 16, 9, 0);
        Assert.False(ScheduleMath.IsDueInWindow(disabled, due.AddSeconds(-30), due));

        var invalid = new ScheduledTask { Hour = 99, Minute = 0 };
        Assert.False(ScheduleMath.IsDueInWindow(invalid, due.AddSeconds(-30), due));
    }

    [Fact]
    public void ExpertDirectiveReachesTheModel()
    {
        var prompt = PromptComposer.Compose(
            "整理这份记录",
            null,
            expertDirective: "你是会议纪要整理助手，待办要标负责人");

        Assert.Contains("本次启用的专家设定", prompt);
        Assert.Contains("待办要标负责人", prompt);
        Assert.Contains("整理这份记录", prompt);
    }

    [Fact]
    public void SceneStyleAndExpertCanCoexist()
    {
        var prompt = PromptComposer.Compose(
            "写份周报",
            null,
            mode: WorkModeText.Office,
            style: WorkStyleText.Plan,
            expertDirective: "你是务实的周报写手");

        Assert.Contains("当前工作模式：办公", prompt);
        Assert.Contains("本次工作模式：计划", prompt);
        Assert.Contains("本次启用的专家设定", prompt);
        Assert.Contains("写份周报", prompt);
    }

    [Fact]
    public void WithoutAnyDirectiveThePromptStaysAsTyped()
    {
        Assert.Equal("原话", PromptComposer.Compose("原话", null, expertDirective: null));
        Assert.Equal("原话", PromptComposer.Compose("原话", null, expertDirective: "   "));
    }

    // ── 按任务绑定模型 / 专家 / 权限 / 项目 ──

    [Fact]
    public void TaskRunConfigResolvesProjectExpertPermissionAndWorkspace()
    {
        var data = new WorkbenchData();
        data.Projects.Add(new Project { Id = "p1", Name = "Q4", WorkspacePath = @"D:/work/Q4" });
        data.Experts.Add(new ExpertPreset
        {
            Id = "e1", Name = "weekly", Directive = "三段式周报", Enabled = true,
        });

        var task = new ScheduledTask
        {
            Name = "周报",
            ProjectId = "p1",
            ExpertId = "e1",
            ModelProfileId = "m2",
            Permission = TaskPermissionText.ReadOnly,
            NotifyChannel = TaskNotificationText.File,
        };

        var config = TaskRunConfig.From(task, data);

        Assert.Equal("Q4", config.ProjectName);
        Assert.Equal(@"D:/work/Q4", config.WorkspacePath);
        Assert.Equal("三段式周报", config.ExpertDirective);
        Assert.Equal("m2", config.ModelProfileId);
        Assert.Equal(PermissionMode.ReadOnly, config.Permission);
        Assert.Equal(TaskNotificationText.File, config.NotifyChannel);
    }

    [Fact]
    public void ExplicitTaskWorkspaceWinsOverProjectWorkspace()
    {
        var data = new WorkbenchData();
        data.Projects.Add(new Project { Id = "p1", WorkspacePath = @"D:/work/Q4" });

        var config = TaskRunConfig.From(new ScheduledTask
        {
            ProjectId = "p1",
            WorkspacePath = @"D:/other",
        }, data);

        Assert.Equal(@"D:/other", config.WorkspacePath);
        Assert.Equal("p1", config.ProjectId);
    }

    [Fact]
    public void DeletedProjectOrExpertFallsBackCleanly()
    {
        var config = TaskRunConfig.From(new ScheduledTask
        {
            ProjectId = "gone",
            ExpertId = "gone",
        }, new WorkbenchData());

        Assert.Null(config.ProjectId);
        Assert.Null(config.ExpertDirective);
        Assert.Null(config.Permission);
        Assert.Equal(TaskNotificationText.System, config.NotifyChannel);
    }

    [Fact]
    public void PermissionTextResolvesOnlyExplicitOverrides()
    {
        Assert.Null(TaskPermissionText.Resolve(TaskPermissionText.Default));
        Assert.Null(TaskPermissionText.Resolve(null));
        Assert.Equal(PermissionMode.ReadOnly, TaskPermissionText.Resolve(TaskPermissionText.ReadOnly));
        Assert.Equal(PermissionMode.FullAccess, TaskPermissionText.Resolve(TaskPermissionText.FullAccess));
    }

    [Fact]
    public void RunSettingsOverrideDoesNotMutateGlobalSettings()
    {
        var settings = new AssistantSettings();
        settings.Models.Add(ModelProfile.Create(AgentProviderKind.DeepSeek, "A", usePresetDefaults: true));
        settings.Models.Add(ModelProfile.Create(AgentProviderKind.OpenAI, "B", usePresetDefaults: true));
        settings.NormalizeModels();
        settings.ActivateModel(settings.Models[0].Id);
        settings.Permission = PermissionMode.Workspace;
        var originalModel = settings.Model;
        var originalId = settings.ActiveModelId;

        var overridden = RunSettingsOverride.Apply(
            settings, settings.Models[1].Id, PermissionMode.FullAccess);

        Assert.NotSame(settings, overridden);
        Assert.Equal(settings.Models[1].Model, overridden.Model);
        Assert.Equal(settings.Models[1].Id, overridden.ActiveModelId);
        Assert.Equal(PermissionMode.FullAccess, overridden.Permission);
        Assert.Equal(originalModel, settings.Model);
        Assert.Equal(originalId, settings.ActiveModelId);
        Assert.Equal(PermissionMode.Workspace, settings.Permission);
    }

    [Fact]
    public void RunSettingsOverrideKeepsSameInstanceWhenNothingChanges()
    {
        var settings = new AssistantSettings { Permission = PermissionMode.Workspace };
        Assert.Same(settings, RunSettingsOverride.Apply(settings, null, PermissionMode.Workspace));
        Assert.Same(settings, RunSettingsOverride.Apply(settings, settings.ActiveModelId, PermissionMode.Workspace));
    }

    [Fact]
    public void UnknownModelProfileFallsBackToCurrentModel()
    {
        var settings = new AssistantSettings();
        settings.Models.Add(ModelProfile.Create(AgentProviderKind.DeepSeek, "A", usePresetDefaults: true));
        settings.NormalizeModels();

        var overridden = RunSettingsOverride.Apply(settings, "does-not-exist", PermissionMode.ReadOnly);

        Assert.Equal(settings.Model, overridden.Model);
        Assert.Equal(PermissionMode.ReadOnly, overridden.Permission);
    }

    // ── 专家包 ──

    [Fact]
    public void ExpertPackExportsOnlyCustomExpertsAndRejectsForeignPacks()
    {
        var experts = new List<ExpertPreset>
        {
            new() { Id = "b1", Name = "内置的", Directive = "x", BuiltIn = true },
            new() { Id = "c1", Name = "我的", Directive = "y", BuiltIn = false },
            new() { Id = "c2", Name = "空的", Directive = "   ", BuiltIn = false },
        };

        var pack = ExpertPack.Create(experts);
        var restored = ExpertPack.Normalize(pack);

        Assert.Single(pack.Experts);
        Assert.Equal("我的", Assert.Single(restored).Name);
        Assert.False(restored[0].BuiltIn);
        Assert.NotEqual("c1", restored[0].Id);

        Assert.Empty(ExpertPack.Normalize(null));
        Assert.Empty(ExpertPack.Normalize(new ExpertPack.Pack { Kind = "not-ours" }));
    }

    // ── 项目实体 ──

    [Fact]
    public async Task ProjectEntityRoundTripsAndScheduledTasksCanBeDetached()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ava-tests", Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);

        var store = new JsonWorkbenchStore(directory);
        await store.LoadAsync();
        store.Data.Projects.Add(new Project
        {
            Id = "p1", Name = "Q4 预算", Description = "季度预算", WorkspacePath = @"D:/work/Q4",
        });
        store.Data.ScheduledTasks.Add(new ScheduledTask { Id = "t1", ProjectId = "p1" });
        await store.SaveAsync();

        var reloaded = new JsonWorkbenchStore(directory);
        await reloaded.LoadAsync();
        var project = Assert.Single(reloaded.Data.Projects);
        Assert.Equal("Q4 预算", project.Name);
        Assert.Equal("季度预算", project.Description);
        Assert.Equal(@"D:/work/Q4", project.WorkspacePath);
        Assert.Equal("p1", Assert.Single(reloaded.Data.ScheduledTasks).ProjectId);

        Directory.Delete(directory, recursive: true);
    }
}
