using System.Runtime.InteropServices;
using System.Text;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent.Tools;

internal sealed class SystemInfoTool : IAgentTool
{
    public AgentToolInfo Info { get; } = new(
        "system_info",
        "查看系统信息",
        "读取操作系统版本、CPU 核心数、内存和磁盘占用。用户问「这台电脑什么配置」时使用。",
        "系统",
        RequiresApproval: false,
        Parameters: []);

    public Task<string> ExecuteAsync(ToolInvocation invocation, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"操作系统：{RuntimeInformation.OSDescription}（{RuntimeInformation.OSArchitecture}）");
        builder.AppendLine($"计算机名：{Environment.MachineName}");
        builder.AppendLine($"当前用户：{Environment.UserName}");
        builder.AppendLine($"处理器：{Environment.ProcessorCount} 个逻辑核心");

        if (OperatingSystem.IsWindows() && TryGetWindowsMemory(out var total, out var available))
        {
            builder.AppendLine($"物理内存：{ToolText.HumanSize((long)total)}（可用 {ToolText.HumanSize((long)available)}）");
        }
        else
        {
            builder.AppendLine($"进程可用内存：{ToolText.HumanSize(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes)}");
        }

        builder.AppendLine();
        builder.AppendLine("磁盘：");
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady)
                {
                    continue;
                }

                builder.AppendLine(
                    $"- {drive.Name} {drive.DriveFormat} 总容量 {ToolText.HumanSize(drive.TotalSize)}，剩余 {ToolText.HumanSize(drive.AvailableFreeSpace)}");
            }
            catch (IOException)
            {
                // 光驱/网络盘可能不可读，跳过。
            }
        }

        return Task.FromResult(builder.ToString());
    }

    private static bool TryGetWindowsMemory(out ulong total, out ulong available)
    {
        var status = new MemoryStatusEx();
        status.Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        if (GlobalMemoryStatusEx(ref status))
        {
            total = status.TotalPhys;
            available = status.AvailPhys;
            return true;
        }

        total = 0;
        available = 0;
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
