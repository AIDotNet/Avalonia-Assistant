namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 没选工作区的任务落在哪：用户目录下的一个隐藏文件夹，和 .claude / .codex 一样。
/// </summary>
/// <remarks>
/// 以前空工作区直接回落到用户主目录本身，等于「工作区」权限下能读写整个 C:\Users\xxx；
/// 而且任务写的文件散在家里，事后根本找不回来。给普通任务单独一格，权限边界和文件去向都清楚了。
/// </remarks>
public static class PlainWorkspace
{
    public const string FolderName = ".avaloniaassistant";

    /// <summary>普通任务的家。第一次用到时才建，不用普通任务的人不会被凭空塞一个目录。</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        FolderName);

    /// <summary>
    /// 这一轮任务真正读写的根：选了工作区就用它，没选（或那个目录被删了）就落到 <see cref="Root"/>。
    /// </summary>
    public static string Resolve(string? workspacePath)
    {
        if (!string.IsNullOrWhiteSpace(workspacePath) && Directory.Exists(workspacePath))
        {
            return Path.GetFullPath(workspacePath);
        }

        try
        {
            Directory.CreateDirectory(Root);
            return Root;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // 建不出来（只读配置、盘满、路径被占）就别硬拦这一轮，退回用户目录——和旧行为一致，
            // 至少会话能起来，报错留给真正需要写文件的那一步。
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Directory.Exists(home) ? home : Environment.CurrentDirectory;
        }
    }
}
