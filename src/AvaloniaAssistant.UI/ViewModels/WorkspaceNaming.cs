namespace AvaloniaAssistant.UI.ViewModels;

/// <summary>
/// 工作区显示名的统一算法：取目录名，取不到再退回整条路径。
/// 侧栏、输入框、浮层三处都要同一个名字，否则同一个目录会有三种叫法。
/// </summary>
internal static class WorkspaceNaming
{
    public static string ShortName(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var trimmed = path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar);

        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? trimmed : name;
    }
}
