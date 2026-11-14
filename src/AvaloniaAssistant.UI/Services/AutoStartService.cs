namespace AvaloniaAssistant.UI.Services;

/// <summary>
/// 开机自启：在用户的「启动」文件夹里放 / 删一个隐藏窗口的 vbs 启动器。
/// 默认关闭，只有用户在「更多」页明确打开才写；不写注册表、不装服务、不改系统策略。
/// 构造函数可传入目录，验收时用临时目录跑一遍，不碰用户真实的启动项。
/// </summary>
public sealed class AutoStartService
{
    private const string LauncherName = "AvaloniaAssistant.vbs";

    private readonly string _folder;
    private readonly string _exePath;

    public AutoStartService(string? folder = null, string? exePath = null)
    {
        _folder = folder ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        _exePath = exePath ?? Environment.ProcessPath ?? "AvaloniaAssistant.exe";
        LauncherPath = Path.Combine(_folder, LauncherName);
    }

    public string LauncherPath { get; }

    public bool IsEnabled => File.Exists(LauncherPath);

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            Directory.CreateDirectory(_folder);
            // 0 = 隐藏窗口，False = 不等待；避免每次登录弹一个黑框
            var script = $"CreateObject(\"Wscript.Shell\").Run \"\"\"{_exePath.Replace("\"", "\"\"")}\"\"\", 0, False\r\n";
            File.WriteAllText(LauncherPath, script);
            return;
        }

        if (File.Exists(LauncherPath))
        {
            File.Delete(LauncherPath);
        }
    }
}
