using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent.Tools;

/// <summary>
/// 让模型把「打算怎么做」写出来给用户看。
/// <para>
/// 多步任务最大的焦虑是不知道它要干什么：等它跑完三步才发现方向错了，前面全白做。
/// 有了这个工具，用户能在第一步之前就看到清单，做错了也能在第 2 步叫停。
/// </para>
/// <para>
/// 工具本身不碰磁盘，也没副作用，所以不需要审批、只读模式下也能用。
/// </para>
/// </summary>
internal sealed class UpdatePlanTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "update_plan",
        "更新执行计划",
        "开始一个多步任务时先调用一次，把打算做的步骤列出来；之后每完成一步再调用一次，把状态更新。"
        + "每行一步，用 [ ] 待办、[~] 进行中、[x] 已完成标注，例如：\n"
        + "- [x] 查目录\n- [~] 按类型归档\n- [ ] 写清单\n"
        + "单步能完成的小事不用调用。计划会在对话里显示成清单，用户随时能看到进度。",
        "任务",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo(
                "steps",
                "string",
                "完整的计划清单（每次都要给全量，不是只给变化的那一步），每行一条，用 [ ] / [~] / [x] 标状态",
                true),
        ]);

    public Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var text = JsonArgs.RequiredString(invocation.Json, "steps");
        var steps = PlanText.Parse(text);

        if (steps.Count == 0)
        {
            throw new InvalidOperationException(
                "没有解析出任何步骤。每行写一步，用 [ ]、[~]、[x] 标注状态，例如：- [ ] 查目录");
        }

        var done = steps.Count(step => step.Status == PlanStepStatus.Done);
        var current = steps.FirstOrDefault(step => step.Status == PlanStepStatus.InProgress);

        var summary = current is not null
            ? $"计划已更新（{done}/{steps.Count} 完成，当前：{current.Text}）。继续做，做完记得回来更新状态。"
            : $"计划已更新（{done}/{steps.Count} 完成）。继续做，做完记得回来更新状态。";

        return Task.FromResult(summary);
    }
}
