using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// Native AOT 下 <see cref="JsonSerializer"/> 的反射重载无法在运行时拼出泛型 JsonTypeInfo，
/// 会话 / 设置这类 POCO 会直接抛 "... is missing native code or metadata"。
/// 这里用 source-gen 预先生成元数据，只在 <see cref="RuntimeFeature.IsDynamicCodeSupported"/>
/// 为 false（原生映像）时挂到 options 上；JIT / ReadyToRun 分支原样返回，序列化行为不变。
/// 链尾保留原有的反射解析器，让基元、JsonNode 和未登记类型继续可用。
///
/// 做法与 pi.net 1.5.2.1 的 Aot/AotJson.cs 一致；Pi 包内部的类型由它们自己的上下文覆盖，
/// 这里只负责本项目自己的 DTO。
/// </summary>
[JsonSerializable(typeof(AssistantSettings))]
[JsonSerializable(typeof(ChatSession))]
[JsonSerializable(typeof(WorkbenchData))]
[JsonSerializable(typeof(ExpertPack.Pack))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(string[]))]
internal partial class AssistantAotJsonContext : JsonSerializerContext
{
}

public static class AssistantAotJson
{
    public static JsonSerializerOptions Configure(JsonSerializerOptions options)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            options.TypeInfoResolver = JsonTypeInfoResolver.Combine(
                AssistantAotJsonContext.Default,
                options.TypeInfoResolver ?? new DefaultJsonTypeInfoResolver());
        }

        return options;
    }
}
