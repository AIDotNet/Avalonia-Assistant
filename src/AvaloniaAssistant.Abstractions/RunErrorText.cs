namespace AvaloniaAssistant.Abstractions;

/// <summary>失败的大类。界面按它决定给用户哪一条出路。</summary>
public enum RunErrorKind
{
    Unknown,
    Auth,
    ModelNotFound,
    RateLimited,
    Quota,
    LengthLimit,
    Network,
    Timeout,
    ContextTooLong,
}

/// <summary>
/// 把运行时抛出来的原始报错翻译成「人话 + 下一步」。
/// 之前界面上只有一段 SDK 原文和一个「重试」，而 401 是重试多少次都不会好的。
/// </summary>
public sealed record RunErrorInfo(RunErrorKind Kind, string Title, string Hint, string ActionLabel)
{
    public static RunErrorInfo Classify(string? raw)
    {
        var text = (raw ?? string.Empty).ToLowerInvariant();

        // 顺序有讲究：额度不足常带 429，先认出额度，否则会被当成限流
        if (Contains(text, "insufficient_quota", "insufficient quota", "balance", "billing", "quota", "余额", "欠费"))
        {
            return new RunErrorInfo(
                RunErrorKind.Quota,
                "额度用完了",
                "到服务商后台充值，或者换一个 Key。",
                "去设置");
        }

        if (Contains(text, "401", "unauthorized", "invalid api key", "invalid_api_key", "authentication", "api key", "api_key", "鉴权"))
        {
            return new RunErrorInfo(
                RunErrorKind.Auth,
                "API Key 没通过",
                "去设置里确认 Key 是否有效，注意别带上多余的空格或引号。",
                "去设置");
        }

        if (Contains(text, "404", "model_not_found", "no such model", "does not exist", "not found", "找不到模型"))
        {
            return new RunErrorInfo(
                RunErrorKind.ModelNotFound,
                "接口或模型名对不上",
                "检查 Base URL 是否包含正确的版本段（多数是 /v1），以及模型名在这个端点是否存在。",
                "去设置");
        }

        if (Contains(text, "429", "rate limit", "too many requests", "限流", "请求过于频繁"))
        {
            return new RunErrorInfo(
                RunErrorKind.RateLimited,
                "请求太频繁",
                "等几十秒再试；持续失败说明并发或额度被服务商限制了。",
                "重试");
        }

        if (Contains(text, "context length", "context_length", "maximum context", "too many tokens", "reduce the length", "上下文"))
        {
            return new RunErrorInfo(
                RunErrorKind.ContextTooLong,
                "这轮上下文太长了",
                "开一个新任务接着做：旧会话已经超出模型能装下的长度。",
                "新建任务");
        }

        if (Contains(text, "达到长度上限", "max tokens", "max_tokens", "length limit"))
        {
            return new RunErrorInfo(
                RunErrorKind.LengthLimit,
                "回复被长度限制截断",
                "把 Max tokens 调大，或者让我分几次写。",
                "去设置");
        }

        if (Contains(text, "timeout", "timed out", "taskcanceled", "operation canceled", "etimedout", "超时"))
        {
            return new RunErrorInfo(
                RunErrorKind.Timeout,
                "请求超时",
                "网络慢或者服务端没响应，稍后再试一次。",
                "重试");
        }

        if (Contains(
                text,
                "no such host", "name or service", "connection refused", "actively refused", "econnrefused",
                "econnreset", "unreachable", "upstream connect", "network", "ssl", "tls", "certificate", "dns",
                "502", "503", "504",
                "连不上", "无法连接", "积极拒绝", "拒绝连接", "连接被拒绝", "代理"))
        {
            return new RunErrorInfo(
                RunErrorKind.Network,
                "连不上服务端",
                "检查网络或代理：本机代理没开、或者请求被代理拦下时都会这样。地址填的是本机服务时尤其要留意。",
                "重试");
        }

        return new RunErrorInfo(
            RunErrorKind.Unknown,
            "这一轮没跑完",
            "可以重试一次；如果反复失败，去设置里检查模型、地址和 Key。",
            "重试");
    }

    private static bool Contains(string text, params string[] keys)
        => keys.Any(key => text.Contains(key, StringComparison.OrdinalIgnoreCase));
}
