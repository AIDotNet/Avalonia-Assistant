using System.Diagnostics;
using System.Text;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent.Tools;

internal sealed class ProcessListTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "list_processes",
        "查看运行中的进程",
        "按内存占用列出当前占用最高的进程，可用于判断某个程序是否在运行。",
        "进程",
        RequiresApproval: false,
        Parameters:
        [
            new ToolParameterInfo("limit", "integer", "返回条数，默认 20，最多 50", false),
        ]);

    public Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var limit = Math.Clamp(JsonArgs.OptionalInt(invocation.Json, "limit", 20), 1, 50);
        var builder = new StringBuilder();

        var processes = Process.GetProcesses()
            .Select(process =>
            {
                try
                {
                    return new
                    {
                        process.ProcessName,
                        process.Id,
                        Memory = process.WorkingSet64,
                    };
                }
                catch
                {
                    return null;
                }
                finally
                {
                    process.Dispose();
                }
            })
            .Where(item => item is not null)
            .OrderByDescending(item => item!.Memory)
            .Take(limit);

        builder.AppendLine("进程名 | PID | 内存");
        foreach (var process in processes)
        {
            builder.AppendLine($"{process!.ProcessName} | {process.Id} | {ToolText.HumanSize(process.Memory)}");
        }

        return Task.FromResult(builder.ToString());
    }
}

internal sealed class ProcessStopTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "stop_process",
        "结束进程",
        "按 PID 结束进程（含子进程）。属于破坏性操作，执行前必须让用户确认。",
        "进程",
        RequiresApproval: true,
        Parameters:
        [
            new ToolParameterInfo("pid", "integer", "进程 ID，可先用 list_processes 查询", true),
        ])
    {
        IsMutating = true,
    };

    public Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var pid = JsonArgs.OptionalInt(invocation.Json, "pid", -1);
        if (pid <= 0)
        {
            throw new InvalidOperationException("请提供有效的 pid。");
        }

        using var process = Process.GetProcessById(pid);
        process.Kill(entireProcessTree: true);
        process.WaitForExit(5000);
        return Task.FromResult($"已结束进程 {process.ProcessName}（PID {pid}）。");
    }
}
