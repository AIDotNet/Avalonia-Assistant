using System.Diagnostics;
using System.Text;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent.Tools;

internal sealed class RunCommandTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "run_command",
        "执行命令",
        "在工作区里执行一条 shell 命令并返回输出。用于构建、测试、查看目录等。属于高风险操作，执行前必须让用户确认。",
        "执行",
        RequiresApproval: true,
        Parameters:
        [
            new ToolParameterInfo("command", "string", "要执行的完整命令", true),
            new ToolParameterInfo("timeout_seconds", "integer", "超时时间，默认 60 秒，最多 300 秒", false),
        ])
    {
        IsMutating = true,
    };

    public async Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var command = JsonArgs.RequiredString(invocation.Json, "command");
        var timeout = Math.Clamp(JsonArgs.OptionalInt(invocation.Json, "timeout_seconds", 60), 5, 300);

        using var process = new Process { StartInfo = Shell.Command(command, invocation.WorkspacePath) };
        var output = new StringBuilder();
        var error = new StringBuilder();

        process.OutputDataReceived += (_, args) => Append(output, args.Data);
        process.ErrorDataReceived += (_, args) => Append(error, args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeoutSource = new CancellationTokenSource(TimeSpan.FromSeconds(timeout));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutSource.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 进程已退出。
            }

            if (timeoutSource.IsCancellationRequested)
            {
                throw new TimeoutException($"命令执行超过 {timeout} 秒，已终止。\n已产生的输出：\n{ToolText.Cap(output.ToString(), 4000)}");
            }

            throw;
        }

        var builder = new StringBuilder();
        builder.AppendLine($"退出码：{process.ExitCode}");
        if (output.Length > 0)
        {
            builder.AppendLine("标准输出：");
            builder.AppendLine(ToolText.Cap(output.ToString()));
        }

        if (error.Length > 0)
        {
            builder.AppendLine("错误输出：");
            builder.AppendLine(ToolText.Cap(error.ToString()));
        }

        return builder.ToString();
    }

    private static void Append(StringBuilder builder, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (builder)
        {
            if (builder.Length < 200_000)
            {
                builder.AppendLine(line);
            }
        }
    }
}
