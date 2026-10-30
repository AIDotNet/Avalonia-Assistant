using Pi.CodingAgent.Core;
using Pi.CodingAgent.Utils;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 给 pi 的包管理器换一个「能在中国网络里跑完」的进程环境：
/// <list type="bullet">
/// <item>npm 源指向镜像（默认 npmmirror），装依赖不再等 registry.npmjs.org；</item>
/// <item>把指向本机的代理变量摘掉——从别的宿主进程继承来的代理端口经常已经关了，
/// npm 会一直重试到看起来「卡死」（截图里那个黑窗口就是这么来的）；</item>
/// <item>隐藏控制台窗口：默认实现在 Windows 上会弹一个 npm 的黑框；</item>
/// <item>给一个默认超时，装不动要报错，不能让界面一直转。</item>
/// </list>
/// </summary>
public sealed class PiPackageCommandRunner(string? registry) : IPackageCommandRunner
{
    /// <summary>npm 默认会重试很久；这里收紧一点，失败得快一些。</summary>
    private const int FetchRetries = 2;

    private const int FetchTimeoutMs = 60_000;

    private const int DefaultTimeoutMs = 240_000;

    private static readonly string[] ProxyVariables =
    [
        "http_proxy", "https_proxy", "all_proxy",
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY",
    ];

    public Task<string> RunAsync(
        string command,
        IReadOnlyList<string> arguments,
        PackageProcessOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options ??= new PackageProcessOptions();
        var environment = BuildEnvironment(options.Env, registry);
        var effectiveArguments = AdjustArguments(command, arguments);

        return Task.Run(
            () =>
            {
                cancellationToken.ThrowIfCancellationRequested();

                // ChildProcess 在 Windows 上自动处理 npm.cmd 的 cmd /c 包装，别自己拼。
                var result = ChildProcess.SpawnSync(command, effectiveArguments, new SpawnSyncOptions
                {
                    Cwd = options.WorkingDirectory,
                    TimeoutMs = options.TimeoutMs ?? DefaultTimeoutMs,
                    Env = environment,
                    WindowsHide = true,
                });

                if (result.Error is not null)
                {
                    throw result.Error;
                }

                if (result.Status is not 0)
                {
                    throw new InvalidOperationException($"{command} exited {result.Status}: {result.Stderr}");
                }

                return result.Stdout;
            },
            cancellationToken);
    }

    /// <summary>
    /// npm 装包时补 <c>--no-audit --no-fund</c>：audit 会额外打一次 registry 接口，
    /// 大陆网络上这一步经常几十秒没响应，装包就卡在这里。
    /// </summary>
    internal static IReadOnlyList<string> AdjustArguments(string command, IReadOnlyList<string> arguments)
    {
        var isNpm = Path.GetFileNameWithoutExtension(command)
            .Equals("npm", StringComparison.OrdinalIgnoreCase);
        if (!isNpm)
        {
            return arguments;
        }

        var touchesRegistry = arguments.Any(argument =>
            argument is "install" or "i" or "update" or "upgrade" or "add");
        if (!touchesRegistry)
        {
            return arguments;
        }

        var result = new List<string>(arguments);
        if (!result.Contains("--no-audit"))
        {
            result.Add("--no-audit");
        }

        if (!result.Contains("--no-fund"))
        {
            result.Add("--no-fund");
        }

        return result;
    }

    /// <summary>
    /// 子进程环境是「覆盖层」：值为 null 表示删掉这个变量。
    /// 见 <see cref="SpawnSyncOptions.Env"/>。
    /// </summary>
    internal static Dictionary<string, string?> BuildEnvironment(
        IReadOnlyDictionary<string, string?>? overlay,
        string? registry)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in ProxyVariables)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (!string.IsNullOrWhiteSpace(value) && IsLoopbackProxy(value))
            {
                environment[name] = null;
            }
        }

        if (!string.IsNullOrWhiteSpace(registry))
        {
            environment["npm_config_registry"] = registry.Trim();
            environment["npm_config_fetch_retries"] = FetchRetries.ToString(System.Globalization.CultureInfo.InvariantCulture);
            environment["npm_config_fetch_timeout"] = FetchTimeoutMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        // pi 自己传的 env 优先级最高（例如 git 的 GIT_TERMINAL_PROMPT）。
        if (overlay is not null)
        {
            foreach (var (key, value) in overlay)
            {
                environment[key] = value;
            }
        }

        return environment;
    }

    /// <summary>
    /// 只摘「本机代理」。公司里的远程代理是用户真的在用的，不动；
    /// 指向 127.0.0.1 的基本是别的宿主进程注入的临时端口，进程一退就接不通。
    /// </summary>
    internal static bool IsLoopbackProxy(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return false;
        }

        return uri.IsLoopback
               || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
