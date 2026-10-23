using System.Diagnostics;
using System.Text;

namespace AvaloniaAssistant.Agent.Tools;

internal static class Shell
{
    public static void Open(string target)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsWindows())
        {
            info = new ProcessStartInfo(target) { UseShellExecute = true };
        }
        else if (OperatingSystem.IsMacOS())
        {
            info = new ProcessStartInfo("open", [target]) { UseShellExecute = false };
        }
        else
        {
            info = new ProcessStartInfo("xdg-open", [target]) { UseShellExecute = false };
        }

        Process.Start(info);
    }

    public static ProcessStartInfo Command(string command, string? workingDirectory)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", ["/d", "/s", "/c", command])
            : new ProcessStartInfo("/bin/bash", ["-lc", command]);

        info.UseShellExecute = false;
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        info.RedirectStandardInput = true;
        info.CreateNoWindow = true;
        info.StandardOutputEncoding = Encoding.UTF8;
        info.StandardErrorEncoding = Encoding.UTF8;

        if (!string.IsNullOrWhiteSpace(workingDirectory) && Directory.Exists(workingDirectory))
        {
            info.WorkingDirectory = workingDirectory;
        }

        return info;
    }
}
