using System.Reflection;

namespace AvaloniaAssistant.UI.Services;

/// <summary>
/// 界面显示的版本号。唯一来源是 <c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c>。
/// <para>
/// 之前侧栏和设置页各硬编码了一份 <c>1.0.0</c>，发版得改两遍——漏一处就会出现
/// 「侧栏 v1.0.0、关于里 1.0.1」这种自己跟自己打架的情况，而且编译不会报错。
/// </para>
/// </summary>
internal static class AppVersion
{
    /// <summary>
    /// 侧栏那种带前缀的写法：<c>v1.0.1</c>。
    /// 必须是表达式体而不是字段初始化：静态字段按声明顺序求值，写成
    /// <c>{ get; } = "v" + Number</c> 且排在 Number 之前时，求值那一刻 Number 还是 null，
    /// 界面上就只剩一个 "v"。这种错编译不报，只能盯着看。
    /// </summary>
    public static string Display => "v" + Number;

    /// <summary>纯版本号：<c>1.0.1</c></summary>
    public static string Number { get; } = Read();

    private static string Read()
    {
        var informational = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        if (string.IsNullOrWhiteSpace(informational))
        {
            // 理论上不会走到这里：Directory.Build.props 里定了 Version
            return "0.0.0";
        }

        // SourceLink 会在版本后面接 +<commit sha>，界面上不显示那一截
        var plus = informational.IndexOf('+');
        return plus > 0 ? informational[..plus] : informational;
    }
}
