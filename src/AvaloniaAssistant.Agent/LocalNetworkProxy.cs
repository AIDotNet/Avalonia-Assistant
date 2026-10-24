using System.Net;
using System.Net.Sockets;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 本机与内网地址不走系统代理。
/// Clash 这类工具把 127.0.0.1 也接管时，Ollama / vLLM / 内网网关都会被退回
/// 「502 upstream connect failed」——明明服务就在本机，却像是网络坏了。
/// 做法只有一层：把 <see cref="HttpClient.DefaultProxy"/> 包起来，本机地址直连，其余照旧。
/// 必须在任何 HttpClient 被创建之前调用一次（应用启动第一行）。
/// </summary>
public static class LocalNetworkProxy
{
    private static bool _applied;

    public static void Apply()
    {
        if (_applied)
        {
            return;
        }

        _applied = true;

        try
        {
            var inner = HttpClient.DefaultProxy;
            if (inner is LocalBypassProxy)
            {
                return;
            }

            HttpClient.DefaultProxy = new LocalBypassProxy(inner);
        }
        catch (PlatformNotSupportedException)
        {
            // 个别平台不允许替换默认代理：保持系统行为，不影响其它功能
        }
    }

    /// <summary>这个目标地址是否应该绕过代理、直接连。</summary>
    public static bool ShouldBypass(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return false;
        }

        // localhost / 127.x / [::1]
        if (uri.IsLoopback)
        {
            return true;
        }

        var host = uri.Host;
        if (host.Length == 0)
        {
            return false;
        }

        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".lan", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var address))
        {
            return IsPrivate(address);
        }

        // 不带点的主机名（ollama、nas、gateway）基本都指向本机或局域网
        return !host.Contains('.');
    }

    private static bool IsPrivate(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            return address.IsIPv6LinkLocal || address.IsIPv6SiteLocal;
        }

        var bytes = address.GetAddressBytes();
        return bytes[0] switch
        {
            10 => true,
            127 => true,
            169 => bytes[1] == 254,          // link-local
            172 => bytes[1] is >= 16 and <= 31,
            192 => bytes[1] == 168,
            _ => false,
        };
    }
}

/// <summary>
/// 只改写本机 / 内网地址的代理选择，其它地址原样交回系统代理。
/// 注意判断的是「目标地址」而不是代理地址：本机代理（127.0.0.1:7897）代理远端 API 仍然照常工作。
/// </summary>
internal sealed class LocalBypassProxy(IWebProxy inner) : IWebProxy
{
    public ICredentials? Credentials
    {
        get => inner.Credentials;
        set => inner.Credentials = value;
    }

    public bool IsBypassed(Uri host)
        => LocalNetworkProxy.ShouldBypass(host) || inner.IsBypassed(host);

    public Uri? GetProxy(Uri destination)
        => LocalNetworkProxy.ShouldBypass(destination) ? destination : inner.GetProxy(destination);
}
