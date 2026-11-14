using System.Text.Json;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Agent;

/// <summary>
/// 工具参数、参数展示这类「声明类型是 object」的序列化路径。
/// JIT 下等价于普通的 <c>new JsonSerializerOptions(...)</c>；
/// Native AOT 下由 <see cref="AssistantAotJson.Configure"/> 挂上 source-gen 上下文，
/// 避免运行时按 <c>value.GetType()</c> 拼泛型 JsonTypeInfo 时缺本机代码。
/// </summary>
internal static class AgentJson
{
    public static readonly JsonSerializerOptions Indented =
        AssistantAotJson.Configure(new JsonSerializerOptions { WriteIndented = true });

    public static readonly JsonSerializerOptions Plain =
        AssistantAotJson.Configure(new JsonSerializerOptions());
}
