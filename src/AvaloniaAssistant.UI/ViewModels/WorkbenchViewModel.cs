using System.Collections.ObjectModel;
using System.Text.Json;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Storage;
using AvaloniaAssistant.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AvaloniaAssistant.UI.ViewModels;

public enum WorkbenchSection
{
    Projects,
    Experts,
    Scheduled,
    Library,
    More,
}

public sealed class ProjectRow
{
    public required Project Project { get; init; }

    public int TaskCount { get; init; }

    public string Name => Project.Name;

    public string Path => Project.WorkspacePath ?? string.Empty;

    public string PathShort => string.IsNullOrWhiteSpace(Path) ? "未设置工作目录" : Ui.ShortenPath(Path);

    public string Description => Project.Description ?? string.Empty;

    public bool HasDescription => Description.Length > 0;

    public string TaskCountText => $"{TaskCount} 条任务";
}

/// <summary>定时任务表单里的一颗可选芯片。</summary>
public sealed class TaskChoice
{
    public required string Group { get; init; }

    public required string Key { get; init; }

    public required string Label { get; init; }

    public bool IsSelected { get; init; }
}

public sealed class ChoiceGroup
{
    public required string Label { get; init; }

    public required ObservableCollection<TaskChoice> Choices { get; init; }
}

public sealed record ScheduledTemplate(string Name, string Description, string Prompt, ScheduleFrequency Frequency, int Hour)
{
    public string FrequencyText => ScheduleFrequencyText.Describe(Frequency);
}

public sealed class ScheduledTaskRow
{
    public required ScheduledTask Task { get; init; }

    public string Name => Task.Name;

    public string PromptPreview => Task.Prompt.Length <= 44
        ? Task.Prompt
        : Task.Prompt[..44] + "…";

    public string ScheduleText => ScheduleMath.DescribeNextRun(Task);

    public string WorkspaceText => string.IsNullOrWhiteSpace(Task.WorkspacePath)
        ? "沿用当前工作区"
        : Ui.ShortenPath(Task.WorkspacePath);

    /// <summary>「本地 / 项目」来源角标，和 WorkBuddy 的任务来源一致。</summary>
    public string SourceText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Task.ProjectId))
            {
                return "本地";
            }

            return "项目";
        }
    }

    public string ConfigText
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Task.ModelProfileId)) parts.Add("专用模型");
            if (!string.IsNullOrWhiteSpace(Task.ExpertId)) parts.Add("专用专家");
            if (TaskPermissionText.Resolve(Task.Permission) is not null)
                parts.Add(TaskPermissionText.Describe(Task.Permission));
            parts.Add(TaskNotificationText.Describe(Task.NotifyChannel));
            return string.Join(" · ", parts);
        }
    }

    public string LastRunText => Task.LastRunAt is { } at
        ? $"{at.LocalDateTime:MM-dd HH:mm} · {Task.LastResult ?? "已触发"}"
        : "还没执行过";

    public string ToggleLabel => Task.Enabled ? "停用" : "启用";
}

public sealed class LibraryRow
{
    public required LibraryItem Item { get; init; }

    public string Name => Item.Name;

    public string PathShort => Ui.ShortenPath(Item.Path);

    public string TypeText => Item.IsDirectory ? "文件夹" : "文件";

    public string AddedText => Item.AddedAt.LocalDateTime.ToString("MM-dd HH:mm");

    public bool Exists => Item.IsDirectory ? Directory.Exists(Item.Path) : File.Exists(Item.Path);

    public bool CanPreview => Exists && !Item.IsDirectory && Ui.CanPreview(Item.Path);
}

public sealed record ShortcutRow(string Keys, string Description);

/// <summary>
/// WorkBuddy 侧栏剩下的几项（项目 / 专家 / 定时任务 / 资料库 / 更多）共用的工作台。
/// 每一项都有真实数据和真实动作：项目读空间列表、专家注入本轮提问、定时任务真的按时间发起新任务、
/// 资料库真的增删文件并引用进对话、更多给设置/数据目录/快捷键的直达入口。不是空壳导航。
/// </summary>
public sealed partial class WorkbenchViewModel : ObservableObject, IDisposable
{
    private readonly MainWindowViewModel _host;
    private bool _loaded;

    public WorkbenchViewModel(MainWindowViewModel host, WorkbenchSection section)
    {
        _host = host;
        Section = section;
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        await _host.WorkbenchReady;
        _loaded = true;
        if (_host.ScheduledRunner is { } runner)
        {
            runner.RunsChanged += OnRunsChanged;
        }

        // 存档里记着「要开机自启」但启动器被外部删了：这里补回来，界面和磁盘保持一致。
        if (_host.Workbench.Data.AutoStart && !_autoStart.IsEnabled)
        {
            try
            {
                _autoStart.SetEnabled(true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // 启动项不可写不拖垮页面：开关显示真实状态，用户点一次会看到原因
            }
        }

        Refresh();
        OnPropertyChanged(nameof(IsAutoStartEnabled));
        OnPropertyChanged(nameof(AutoStartLabel));
        OnPropertyChanged(nameof(IsReady));
    }

    public bool IsReady => _loaded;

    private void OnRunsChanged(object? sender, EventArgs e)
    {
        Ui.Post(RefreshRuns);
    }

    public void Dispose()
    {
        if (_host.ScheduledRunner is { } runner)
        {
            runner.RunsChanged -= OnRunsChanged;
        }
    }

    // ── 分区 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProjects))]
    [NotifyPropertyChangedFor(nameof(IsExperts))]
    [NotifyPropertyChangedFor(nameof(IsScheduled))]
    [NotifyPropertyChangedFor(nameof(IsLibrary))]
    [NotifyPropertyChangedFor(nameof(IsMore))]
    [NotifyPropertyChangedFor(nameof(Title))]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private WorkbenchSection section;

    public bool IsProjects => Section == WorkbenchSection.Projects;
    public bool IsExperts => Section == WorkbenchSection.Experts;
    public bool IsScheduled => Section == WorkbenchSection.Scheduled;
    public bool IsLibrary => Section == WorkbenchSection.Library;
    public bool IsMore => Section == WorkbenchSection.More;

    public string Title => Section switch
    {
        WorkbenchSection.Projects => "项目",
        WorkbenchSection.Experts => "专家",
        WorkbenchSection.Scheduled => "定时任务",
        WorkbenchSection.Library => "资料库",
        _ => "设置与关于",
    };

    public string Subtitle => Section switch
    {
        WorkbenchSection.Projects => "按工作空间归类：一个事项一个目录，输入和产出分开放",
        WorkbenchSection.Experts => "选一套专家设定，之后每一轮提问都会带上它",
        WorkbenchSection.Scheduled => "按设定时间自动发起任务，结果集中留在运行记录里",
        WorkbenchSection.Library => "集中管理本机资料，可预览、打开，或直接引用进任务",
        _ => "版本、数据目录、快捷键与开机自启",
    };

    [RelayCommand]
    private void SelectSection(string name)
    {
        Section = name switch
        {
            "projects" => WorkbenchSection.Projects,
            "experts" => WorkbenchSection.Experts,
            "scheduled" => WorkbenchSection.Scheduled,
            "library" => WorkbenchSection.Library,
            _ => WorkbenchSection.More,
        };
    }

    [RelayCommand]
    private void Back() => _host.GoBack();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    private string? notice;

    public bool HasNotice => !string.IsNullOrWhiteSpace(Notice);

    private void Say(string text) => Notice = text;

    // ── 项目 ──

    public ObservableCollection<ProjectRow> Projects { get; } = [];

    public bool HasProjects => Projects.Count > 0;

    private void RefreshProjects()
    {
        Projects.Clear();
        foreach (var project in _host.Workbench.Data.Projects.OrderByDescending(item => item.CreatedAt))
        {
            var count = string.IsNullOrWhiteSpace(project.WorkspacePath)
                ? 0
                : _host.Workspaces
                    .FirstOrDefault(workspace => string.Equals(
                        workspace.Path, project.WorkspacePath, StringComparison.OrdinalIgnoreCase))
                    ?.Sessions.Count ?? 0;
            Projects.Add(new ProjectRow { Project = project, TaskCount = count });
        }

        OnPropertyChanged(nameof(HasProjects));
        RebuildTaskChoices();
    }

    [RelayCommand]
    private void OpenProject(ProjectRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Path) || !Ui.OpenExternal(row.Path))
        {
            Say("这个还没设置可打开的目录。");
        }
    }

    [RelayCommand]
    private void RevealProject(ProjectRow row)
    {
        if (string.IsNullOrWhiteSpace(row.Path) || !Ui.RevealInExplorer(row.Path))
        {
            Say("这个还没设置可定位的目录。");
        }
    }

    [RelayCommand]
    private async Task NewTaskInProjectAsync(ProjectRow row)
        => await _host.StartProjectTaskAsync(row.Project);

    // ── 项目 CRUD ──

    [ObservableProperty]
    private bool showProjectForm;

    [ObservableProperty]
    private string newProjectName = string.Empty;

    [ObservableProperty]
    private string newProjectDescription = string.Empty;

    [ObservableProperty]
    private string newProjectWorkspace = string.Empty;

    [RelayCommand]
    private void ToggleProjectForm() => ShowProjectForm = !ShowProjectForm;

    [RelayCommand]
    private async Task PickProjectWorkspaceAsync()
    {
        var path = await _host.Picker.PickFolderAsync("选择项目的工作目录");
        if (!string.IsNullOrWhiteSpace(path))
        {
            NewProjectWorkspace = path;
        }
    }

    [RelayCommand]
    private async Task CreateProjectAsync()
    {
        var name = NewProjectName.Trim();
        if (name.Length == 0)
        {
            Say("项目名称不能为空。");
            return;
        }

        if (_host.Workbench.Data.Projects.Any(item =>
                string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            Say("已经有同名项目了，换一个名字。");
            return;
        }

        var path = NewProjectWorkspace.Trim();
        _host.Workbench.Data.Projects.Add(new Project
        {
            Name = name,
            Description = string.IsNullOrWhiteSpace(NewProjectDescription) ? null : NewProjectDescription.Trim(),
            WorkspacePath = path.Length == 0 ? null : path,
        });

        if (path.Length > 0 && !Directory.Exists(path))
        {
            try
            {
                Directory.CreateDirectory(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Say("项目建好了，但这个目录暂时建不出来：" + ex.Message);
            }
        }

        await _host.Workbench.SaveAsync();
        NewProjectName = string.Empty;
        NewProjectDescription = string.Empty;
        NewProjectWorkspace = string.Empty;
        ShowProjectForm = false;
        RefreshProjects();
        Say($"已创建项目「{name}」。定时任务可以挂到它下面。");
    }

    [RelayCommand]
    private async Task DeleteProjectAsync(ProjectRow row)
    {
        _host.Workbench.Data.Projects.Remove(row.Project);
        foreach (var task in _host.Workbench.Data.ScheduledTasks
                     .Where(item => item.ProjectId == row.Project.Id))
        {
            task.ProjectId = null;
        }

        await _host.Workbench.SaveAsync();
        RefreshProjects();
        RefreshScheduled();
        Say($"已删除项目「{row.Name}」（磁盘上的文件没动，挂在它下面的定时任务改为本地任务）。");
    }

    // ── 专家 ──

    public ObservableCollection<ExpertPreset> Experts { get; } = [];

    public string ActiveExpertLabel
    {
        get
        {
            var active = _host.Workbench.Data.Experts.FirstOrDefault(
                item => item.Enabled && item.Id == _host.Workbench.Data.ActiveExpertId);
            return active is null ? "未启用专家" : $"正在使用：{active.Name}";
        }
    }

    private void RefreshExperts()
    {
        Experts.Clear();
        foreach (var expert in _host.Workbench.Data.Experts)
        {
            Experts.Add(expert);
        }

        OnPropertyChanged(nameof(ActiveExpertLabel));
    }

    [RelayCommand]
    private async Task SelectExpertAsync(ExpertPreset preset)
    {
        _host.Workbench.Data.ActiveExpertId = preset.Id;
        _host.SetActiveExpert(preset);
        await _host.Workbench.SaveAsync();
        RefreshExperts();
        Say($"已启用「{preset.Name}」：之后每一轮提问都会带上这套设定。");
    }

    [RelayCommand]
    private async Task ClearExpertAsync()
    {
        _host.Workbench.Data.ActiveExpertId = null;
        _host.SetActiveExpert(null);
        await _host.Workbench.SaveAsync();
        RefreshExperts();
        Say("已取消专家设定。");
    }

    [ObservableProperty]
    private bool showExpertForm;

    [ObservableProperty]
    private string newExpertName = string.Empty;

    [ObservableProperty]
    private string newExpertDescription = string.Empty;

    [ObservableProperty]
    private string newExpertDirective = string.Empty;

    [RelayCommand]
    private void ToggleExpertForm() => ShowExpertForm = !ShowExpertForm;

    [RelayCommand]
    private async Task AddExpertAsync()
    {
        var name = NewExpertName.Trim();
        var directive = NewExpertDirective.Trim();
        if (name.Length == 0 || directive.Length == 0)
        {
            Say("名称和专家指令都要填，指令是真正发给模型的内容。");
            return;
        }

        _host.Workbench.Data.Experts.Add(new ExpertPreset
        {
            Name = name,
            Description = NewExpertDescription.Trim(),
            Directive = directive,
            BuiltIn = false,
            Enabled = true,
        });

        await _host.Workbench.SaveAsync();
        NewExpertName = string.Empty;
        NewExpertDescription = string.Empty;
        NewExpertDirective = string.Empty;
        ShowExpertForm = false;
        RefreshExperts();
        Say($"已添加专家「{name}」，点卡片即可启用。");
    }

    [RelayCommand]
    private async Task DeleteExpertAsync(ExpertPreset preset)
    {
        if (preset.BuiltIn)
        {
            Say("内置专家不能删；不想用就点「取消专家设定」。");
            return;
        }

        _host.Workbench.Data.Experts.Remove(preset);
        if (_host.Workbench.Data.ActiveExpertId == preset.Id)
        {
            _host.Workbench.Data.ActiveExpertId = null;
            _host.SetActiveExpert(null);
        }

        await _host.Workbench.SaveAsync();
        RefreshExperts();
        RebuildTaskChoices();
        Say($"已删除专家「{preset.Name}」。");
    }

    // ── 专家包：本地的「专家市场」等价物，导入导出不依赖任何云端服务 ──

    [RelayCommand]
    private async Task ExportExpertPackAsync()
    {
        var pack = ExpertPack.Create(_host.Workbench.Data.Experts);
        if (pack.Experts.Count == 0)
        {
            Say("还没有自定义专家可以导出；内置档位不随包走。");
            return;
        }

        var path = await _host.Picker.PickSaveFileAsync("导出专家包", "experts.json", "json");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(pack, AssistantJson.Options));
        Say($"已导出 {pack.Experts.Count} 个自定义专家到 {path}。");
    }

    [RelayCommand]
    private async Task ImportExpertPackAsync()
    {
        var path = await _host.Picker.PickFileAsync("导入专家包", "*.json");
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        ExpertPack.Pack? pack;
        try
        {
            var json = await File.ReadAllTextAsync(path);
            pack = JsonSerializer.Deserialize<ExpertPack.Pack>(json, AssistantJson.Options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Say("读不出有效的专家包：" + ex.Message);
            return;
        }

        var items = ExpertPack.Normalize(pack);
        if (items.Count == 0)
        {
            Say("这不是本应用的专家包，或包里没有可用的专家。");
            return;
        }

        foreach (var item in items)
        {
            _host.Workbench.Data.Experts.Add(item);
        }

        await _host.Workbench.SaveAsync();
        RefreshExperts();
        RebuildTaskChoices();
        Say($"已导入 {items.Count} 个专家，可以在卡片上点「启用」。");
    }

    // ── 定时任务 ──

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsScheduledTasksTab))]
    [NotifyPropertyChangedFor(nameof(IsScheduledRunsTab))]
    private string scheduledTab = "tasks";

    public bool IsScheduledTasksTab => ScheduledTab == "tasks";
    public bool IsScheduledRunsTab => ScheduledTab == "runs";

    [RelayCommand]
    private void ShowScheduledTab(string tab) => ScheduledTab = tab;

    public ObservableCollection<ScheduledTaskRow> ScheduledTasks { get; } = [];

    public ObservableCollection<ScheduledRunRecord> RunRecords { get; } = [];

    public bool HasScheduledTasks => ScheduledTasks.Count > 0;

    public bool HasRunRecords => RunRecords.Count > 0;

    /// <summary>常用模板：点一下预填表单，不直接建任务（避免误点就开始跑）。</summary>
    public ObservableCollection<ScheduledTemplate> Templates { get; } =
    [
        new("每日简报", "每天早上汇总工作区里的新文件", "帮我汇总工作区里最近一天新增或改动的文件，列成一份简短的日报。", ScheduleFrequency.Daily, 9),
        new("每周周报", "每周一整理本周进展", "帮我整理这周的项目进展、风险和下周计划，写成一份三段式周报。", ScheduleFrequency.Weekly, 9),
        new("月度归档", "每月 1 号整理目录", "帮我看一下工作区目录，给出这个月的归档建议清单，先不要直接搬文件。", ScheduleFrequency.Monthly, 10),
    ];

    private void RefreshScheduled()
    {
        ScheduledTasks.Clear();
        foreach (var task in _host.Workbench.Data.ScheduledTasks
                     .OrderBy(item => item.Enabled ? 0 : 1)
                     .ThenBy(item => item.Hour)
                     .ThenBy(item => item.Minute))
        {
            ScheduledTasks.Add(new ScheduledTaskRow { Task = task });
        }

        OnPropertyChanged(nameof(HasScheduledTasks));
        RefreshRuns();
    }

    private void RefreshRuns()
    {
        RunRecords.Clear();
        foreach (var record in _host.Workbench.Data.RunRecords.Take(200))
        {
            RunRecords.Add(record);
        }

        OnPropertyChanged(nameof(HasRunRecords));
    }

    [ObservableProperty]
    private bool showTaskForm;

    [ObservableProperty]
    private string taskName = string.Empty;

    [ObservableProperty]
    private string taskPrompt = string.Empty;

    [ObservableProperty]
    private string taskWorkspace = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFreqOnce))]
    [NotifyPropertyChangedFor(nameof(IsFreqDaily))]
    [NotifyPropertyChangedFor(nameof(IsFreqWeekly))]
    [NotifyPropertyChangedFor(nameof(IsFreqMonthly))]
    private int taskFrequencyIndex = 1;

    public bool IsFreqOnce => TaskFrequencyIndex == 0;
    public bool IsFreqDaily => TaskFrequencyIndex == 1;
    public bool IsFreqWeekly => TaskFrequencyIndex == 2;
    public bool IsFreqMonthly => TaskFrequencyIndex == 3;

    [RelayCommand]
    private void SetFrequency(string index)
    {
        if (int.TryParse(index, out var value))
        {
            TaskFrequencyIndex = Math.Clamp(value, 0, 3);
        }
    }

    [ObservableProperty]
    private string taskHourText = "9";

    [ObservableProperty]
    private string taskMinuteText = "0";

    [ObservableProperty]
    private string taskWeekdays = string.Empty;

    [ObservableProperty]
    private string taskMonthDays = string.Empty;

    [ObservableProperty]
    private string taskOnceDate = string.Empty;

    [ObservableProperty]
    private string taskValidTo = string.Empty;

    public string[] FrequencyNames { get; } = ["单次", "每天", "每周", "每月"];

    // ── 附加配置（WorkBuddy 表单里「模型 / 专家 / 技能 / 连接器 / 权限」的本地对应）──

    [ObservableProperty]
    private string taskProjectId = string.Empty;

    [ObservableProperty]
    private string taskModelProfileId = string.Empty;

    [ObservableProperty]
    private string taskExpertId = string.Empty;

    [ObservableProperty]
    private string taskPermission = TaskPermissionText.Default;

    [ObservableProperty]
    private string taskNotifyChannel = TaskNotificationText.System;

    public ObservableCollection<ChoiceGroup> TaskChoiceGroups { get; } = [];

    private void RebuildTaskChoices()
    {
        TaskChoiceGroups.Clear();

        TaskChoiceGroups.Add(new ChoiceGroup
        {
            Label = "所属项目",
            Choices =
            [
                new TaskChoice { Group = "project", Key = "", Label = "本地任务", IsSelected = TaskProjectId.Length == 0 },
                .. _host.Workbench.Data.Projects.Select(item => new TaskChoice
                {
                    Group = "project", Key = item.Id, Label = item.Name,
                    IsSelected = TaskProjectId == item.Id,
                }),
            ],
        });

        TaskChoiceGroups.Add(new ChoiceGroup
        {
            Label = "本任务模型",
            Choices =
            [
                new TaskChoice { Group = "model", Key = "", Label = "沿用当前模型", IsSelected = TaskModelProfileId.Length == 0 },
                .. _host.Settings.Models.Select(item => new TaskChoice
                {
                    Group = "model", Key = item.Id, Label = item.DisplayName,
                    IsSelected = TaskModelProfileId == item.Id,
                }),
            ],
        });

        TaskChoiceGroups.Add(new ChoiceGroup
        {
            Label = "本任务专家",
            Choices =
            [
                new TaskChoice { Group = "expert", Key = "", Label = "沿用当前专家", IsSelected = TaskExpertId.Length == 0 },
                .. _host.Workbench.Data.Experts.Where(item => item.Enabled).Select(item => new TaskChoice
                {
                    Group = "expert", Key = item.Id, Label = item.Name,
                    IsSelected = TaskExpertId == item.Id,
                }),
            ],
        });

        TaskChoiceGroups.Add(new ChoiceGroup
        {
            Label = "执行权限",
            Choices =
            [
                PermissionChoice(TaskPermissionText.Default, "沿用当前档位"),
                PermissionChoice(TaskPermissionText.ReadOnly, "只读"),
                PermissionChoice(TaskPermissionText.FullAccess, "完全访问"),
            ],
        });

        TaskChoiceGroups.Add(new ChoiceGroup
        {
            Label = "结果推送",
            Choices =
            [
                NotifyChoice(TaskNotificationText.System, "系统提醒"),
                NotifyChoice(TaskNotificationText.File, "写入结果文件"),
                NotifyChoice(TaskNotificationText.None, "不推送"),
            ],
        });
    }

    private TaskChoice PermissionChoice(string key, string label) => new()
    {
        Group = "permission", Key = key, Label = label, IsSelected = TaskPermission == key,
    };

    private TaskChoice NotifyChoice(string key, string label) => new()
    {
        Group = "notify", Key = key, Label = label, IsSelected = TaskNotifyChannel == key,
    };

    [RelayCommand]
    private void SelectTaskChoice(TaskChoice choice)
    {
        switch (choice.Group)
        {
            case "project":
                TaskProjectId = choice.Key;
                break;
            case "model":
                TaskModelProfileId = choice.Key;
                break;
            case "expert":
                TaskExpertId = choice.Key;
                break;
            case "permission":
                TaskPermission = choice.Key;
                break;
            case "notify":
                TaskNotifyChannel = choice.Key;
                break;
        }

        RebuildTaskChoices();
    }

    [RelayCommand]
    private void ToggleTaskForm() => ShowTaskForm = !ShowTaskForm;

    [RelayCommand]
    private void ApplyTemplate(ScheduledTemplate template)
    {
        TaskName = template.Name;
        TaskPrompt = template.Prompt;
        TaskFrequencyIndex = (int)template.Frequency;
        TaskHourText = template.Hour.ToString();
        ShowTaskForm = true;
        Say($"已把「{template.Name}」填进表单，改完再点创建。");
    }

    [RelayCommand]
    private void CreateFromTask() => _host.GoHomeAndPrefill("帮我创建一个定时任务：");

    private static List<int> ParseNumbers(string text, int min, int max)
    {
        var result = new List<int>();
        foreach (var part in text.Split([',', '，', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (int.TryParse(part.Trim(), out var value) && value >= min && value <= max && !result.Contains(value))
            {
                result.Add(value);
            }
        }

        return result;
    }

    [RelayCommand]
    private async Task CreateTaskAsync()
    {
        var name = TaskName.Trim();
        var prompt = TaskPrompt.Trim();
        if (name.Length == 0 || prompt.Length == 0)
        {
            Say("名称和提示词都要填；提示词是到时候真正发给模型的内容。");
            return;
        }

        var frequency = (ScheduleFrequency)Math.Clamp(TaskFrequencyIndex, 0, 3);
        var task = new ScheduledTask
        {
            Name = name,
            Prompt = prompt,
            WorkspacePath = string.IsNullOrWhiteSpace(TaskWorkspace) ? null : TaskWorkspace.Trim(),
            ProjectId = string.IsNullOrWhiteSpace(TaskProjectId) ? null : TaskProjectId,
            ModelProfileId = string.IsNullOrWhiteSpace(TaskModelProfileId) ? null : TaskModelProfileId,
            ExpertId = string.IsNullOrWhiteSpace(TaskExpertId) ? null : TaskExpertId,
            Permission = TaskPermission,
            NotifyChannel = TaskNotifyChannel,
            Frequency = frequency,
            Hour = Math.Clamp(int.TryParse(TaskHourText.Trim(), out var hour) ? hour : 9, 0, 23),
            Minute = Math.Clamp(int.TryParse(TaskMinuteText.Trim(), out var minute) ? minute : 0, 0, 59),
            Weekdays = ParseNumbers(TaskWeekdays, 0, 6),
            MonthDays = ParseNumbers(TaskMonthDays, 1, 31),
            OnceDate = frequency == ScheduleFrequency.Once && !string.IsNullOrWhiteSpace(TaskOnceDate)
                ? TaskOnceDate.Trim()
                : DateTime.Now.ToString("yyyy-MM-dd"),
            ValidTo = string.IsNullOrWhiteSpace(TaskValidTo) ? null : TaskValidTo.Trim(),
        };

        _host.Workbench.Data.ScheduledTasks.Add(task);
        await _host.Workbench.SaveAsync();
        ShowTaskForm = false;
        TaskName = string.Empty;
        TaskPrompt = string.Empty;
        TaskWeekdays = string.Empty;
        TaskMonthDays = string.Empty;
        RefreshScheduled();
        Say($"已创建「{name}」。执行权限沿用设置里的档位，无人值守时若用默认权限，遇到需确认的工具会停在等待确认。");
    }

    [RelayCommand]
    private async Task ToggleTaskAsync(ScheduledTaskRow row)
    {
        row.Task.Enabled = !row.Task.Enabled;
        await _host.Workbench.SaveAsync();
        RefreshScheduled();
        Say(row.Task.Enabled ? $"已启用「{row.Name}」。" : $"已停用「{row.Name}」。");
    }

    [RelayCommand]
    private async Task DeleteTaskAsync(ScheduledTaskRow row)
    {
        _host.Workbench.Data.ScheduledTasks.Remove(row.Task);
        await _host.Workbench.SaveAsync();
        RefreshScheduled();
        Say($"已删除「{row.Name}」。");
    }

    [RelayCommand]
    private async Task RunNowAsync(ScheduledTaskRow row)
    {
        if (_host.ScheduledRunner is not { } runner)
        {
            Say("调度器还没就绪，稍等再试。");
            return;
        }

        Say($"正在试跑「{row.Name}」…");
        var record = await runner.TriggerAsync(row.Task);
        RefreshScheduled();
        Say(record.Status == "失败"
            ? $"试跑失败：{record.Detail}"
            : "已试跑一次：新任务已发起，运行记录里可查看。");
    }

    [RelayCommand]
    private async Task ClearRunsAsync()
    {
        _host.Workbench.Data.RunRecords.Clear();
        await _host.Workbench.SaveAsync();
        RefreshRuns();
        Say("运行记录已清空（定时任务本身没动）。");
    }

    // ── 资料库 ──

    public ObservableCollection<LibraryRow> LibraryItems { get; } = [];

    public bool HasLibraryItems => LibraryItems.Count > 0;

    private void RefreshLibrary()
    {
        LibraryItems.Clear();
        foreach (var item in _host.Workbench.Data.LibraryItems.OrderByDescending(entry => entry.AddedAt))
        {
            LibraryItems.Add(new LibraryRow { Item = item });
        }

        OnPropertyChanged(nameof(HasLibraryItems));
    }

    private async Task AddPathsAsync(IEnumerable<string> paths, bool isDirectory)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path)
                || _host.Workbench.Data.LibraryItems.Any(item =>
                    string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _host.Workbench.Data.LibraryItems.Add(new LibraryItem
            {
                Path = path,
                Name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                IsDirectory = isDirectory || Directory.Exists(path),
            });
            added++;
        }

        await _host.Workbench.SaveAsync();
        RefreshLibrary();
        Say(added == 0 ? "没有新增（可能已经加过了）。" : $"已添加 {added} 项到资料库。");
    }

    [RelayCommand]
    private async Task AddLibraryFilesAsync()
    {
        var paths = await _host.Picker.PickFilesAsync("添加文件到资料库");
        await AddPathsAsync(paths, isDirectory: false);
    }

    [RelayCommand]
    private async Task AddLibraryFolderAsync()
    {
        var path = await _host.Picker.PickFolderAsync("添加文件夹到资料库");
        if (!string.IsNullOrWhiteSpace(path))
        {
            await AddPathsAsync([path], isDirectory: true);
        }
    }

    [RelayCommand]
    private async Task RemoveLibraryItemAsync(LibraryRow row)
    {
        _host.Workbench.Data.LibraryItems.Remove(row.Item);
        await _host.Workbench.SaveAsync();
        RefreshLibrary();
        Say($"已从资料库移除「{row.Name}」（磁盘上的文件没删）。");
    }

    [RelayCommand]
    private void OpenLibraryItem(LibraryRow row)
    {
        if (!Ui.OpenExternal(row.Item.Path))
        {
            Say("打不开：" + row.Item.Path);
        }
    }

    [RelayCommand]
    private async Task PreviewLibraryItemAsync(LibraryRow row)
    {
        if (!row.Exists || !await Ui.PreviewDocumentAsync(row.Item.Path))
        {
            Say("这个文件没法在应用内预览，可能已经不在原位置了。");
        }
    }

    [RelayCommand]
    private void RevealLibraryItem(LibraryRow row)
    {
        if (!Ui.RevealInExplorer(row.Item.Path))
        {
            Say("定位不到：" + row.Item.Path);
        }
    }

    [RelayCommand]
    private void ReferenceLibraryItem(LibraryRow row)
    {
        if (!row.Exists)
        {
            Say("这个文件已经不在了，先重新添加。");
            return;
        }

        if (!_host.AddReferenceToReturnChat(row.Item.Path))
        {
            Say("没找到可引用进去的任务，先新建一条任务再来。");
            return;
        }

        _host.GoBack();
    }

    // ── 更多 ──

    public string VersionText => AppVersion.Display;

    public string DataFolderText => Ui.ShortenPath(AppPaths.Root);

    public ObservableCollection<ShortcutRow> Shortcuts { get; } =
    [
        new("Ctrl+N", "新建任务"),
        new("Ctrl+K", "搜索任务"),
        new("Ctrl+F", "在当前会话内查找"),
        new("Ctrl+/", "打开快捷键总览"),
        new("Ctrl+滚轮 / Ctrl+0", "界面缩放 / 还原"),
        new("Enter", "发送（生成期间为排队）"),
        new("Shift+Enter", "换行"),
        new("Ctrl+Enter", "允许一次工具调用"),
        new("Ctrl+Shift+Enter", "本会话都允许"),
        new("Esc", "拒绝待确认的工具调用"),
    ];

    [RelayCommand]
    private async Task OpenSettingsAsync() => await _host.OpenSettingsAsync();

    [RelayCommand]
    private void OpenDataFolder()
    {
        Directory.CreateDirectory(AppPaths.Root);
        if (!Ui.OpenExternal(AppPaths.Root))
        {
            Say("打不开数据目录：" + AppPaths.Root);
        }
    }

    [RelayCommand]
    private async Task CopyDiagnosticsAsync()
    {
        var text = $"Avalonia Assistant {AppVersion.Display}\n"
                   + $"系统：{Environment.OSVersion.VersionString}\n"
                   + $"数据目录：{AppPaths.Root}\n"
                   + $"模型：{_host.Settings.ActiveModel?.DisplayName ?? "(未配置)"}";
        await Ui.CopyTextAsync(text);
        Say("已复制版本与数据目录信息。");
    }

    // ── 开机自启：明确的本地开关，写用户「启动」文件夹，不改注册表 ──

    private readonly AutoStartService _autoStart = new();

    public bool IsAutoStartEnabled => _autoStart.IsEnabled;

    public string AutoStartLabel => IsAutoStartEnabled ? "开机自启：已开启" : "开机自启：已关闭";

    [RelayCommand]
    private async Task ToggleAutoStartAsync()
    {
        var next = !_autoStart.IsEnabled;
        try
        {
            _autoStart.SetEnabled(next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Say("改不了启动项：" + ex.Message);
            return;
        }

        _host.Workbench.Data.AutoStart = next;
        await _host.Workbench.SaveAsync();
        OnPropertyChanged(nameof(IsAutoStartEnabled));
        OnPropertyChanged(nameof(AutoStartLabel));
        Say(next
            ? "已开启开机自启：启动文件夹里放了一个隐藏窗口的启动器，定时任务到点才有机会跑。"
            : "已关闭开机自启，启动器已删除。");
    }

    private void Refresh()
    {
        RefreshProjects();
        RefreshExperts();
        RefreshScheduled();
        RefreshLibrary();
    }
}
