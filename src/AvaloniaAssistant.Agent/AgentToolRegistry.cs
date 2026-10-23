using System.Text.Json;
using AvaloniaAssistant.Abstractions;
using AvaloniaAssistant.Agent.Tools;
using Pi.Agent;
using Pi.Agent.Types;
using Pi.Ai.Types;
using ToolDefinition = Pi.CodingAgent.Core.Extensions.ToolDefinition;

namespace AvaloniaAssistant.Agent;

/// <summary>内置工具清单。UI 通过 <see cref="Tools"/> 展示与开关，运行时按会话权限装配。</summary>
public sealed class AgentToolRegistry
{
    private readonly IReadOnlyList<IAgentTool> _tools;

    public AgentToolRegistry()
        : this(DefaultTools())
    {
    }

    internal AgentToolRegistry(IEnumerable<IAgentTool> tools)
    {
        _tools = tools.ToList();
    }

    public IReadOnlyList<AgentToolInfo> Tools => _tools.Select(tool => tool.Info).ToList();

    internal IReadOnlyList<AgentTool> CreateTools(
        AgentRunRequest request,
        IReadOnlyCollection<string> disabled,
        out IReadOnlyDictionary<string, AgentToolInfo> catalog)
    {
        var enabled = EnabledTools(disabled);
        catalog = enabled.ToDictionary(tool => tool.Info.Name, tool => tool.Info, StringComparer.Ordinal);

        var agentTools = new List<AgentTool>(enabled.Count);
        foreach (var tool in enabled)
        {
            var info = tool.Info;
            var captured = tool;
            agentTools.Add(new AgentTool(
                Name: info.Name,
                Description: BuildDescription(info),
                Parameters: BuildSchema(info),
                Label: info.Label,
                Execute: async (_, args, cancellationToken, _) =>
                {
                    try
                    {
                        var text = await captured.ExecuteAsync(
                            new ToolInvocation(args, request.WorkspacePath, request.Permission),
                            cancellationToken).ConfigureAwait(false);

                        return new AgentToolResult([new TextContent(text)]);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return AgentToolResult.FromError(ex.Message);
                    }
                }));
        }

        return agentTools;
    }

    /// <summary>
    /// 把内置工具包成 Pi.CodingAgent 的 <see cref="ToolDefinition"/>，作为 CustomTools 注入会话。
    /// 参数是字典（不是 JsonElement），执行结果同样是文本块。
    /// </summary>
    internal IReadOnlyList<ToolDefinition> CreateCustomTools(
        AgentRunRequest request,
        IReadOnlyCollection<string> disabled,
        out IReadOnlyDictionary<string, AgentToolInfo> catalog)
    {
        var enabled = EnabledTools(disabled);
        catalog = enabled.ToDictionary(tool => tool.Info.Name, tool => tool.Info, StringComparer.Ordinal);

        var definitions = new List<ToolDefinition>(enabled.Count);
        foreach (var tool in enabled)
        {
            var info = tool.Info;
            var captured = tool;
            definitions.Add(new ToolDefinition
            {
                Name = info.Name,
                Label = info.Label,
                Description = BuildDescription(info),
                Parameters = BuildSchema(info),
                Execute = async (_, args, cancellationToken, _, _) =>
                {
                    try
                    {
                        var text = await captured.ExecuteAsync(
                            new ToolInvocation(args, request.WorkspacePath, request.Permission),
                            cancellationToken).ConfigureAwait(false);

                        return new AgentToolResult([new TextContent(text)]);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        return AgentToolResult.FromError(ex.Message);
                    }
                },
            });
        }

        return definitions;
    }

    private List<IAgentTool> EnabledTools(IReadOnlyCollection<string> disabled)
        => _tools.Where(tool => !disabled.Contains(tool.Info.Name, StringComparer.Ordinal)).ToList();

    private static string BuildDescription(AgentToolInfo info)
    {
        var parameters = info.Parameters.Count == 0
            ? string.Empty
            : "\n参数：" + string.Join("；", info.Parameters.Select(p =>
                $"{p.Name}({p.Type}{(p.Required ? "，必填" : string.Empty)}) {p.Description}"));

        return info.Description + parameters;
    }

    private static ToolSchema BuildSchema(AgentToolInfo info)
    {
        var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
        var required = new List<string>();

        foreach (var parameter in info.Parameters)
        {
            properties[parameter.Name] = new Dictionary<string, object?>
            {
                ["type"] = parameter.Type,
                ["description"] = parameter.Description,
            };

            if (parameter.Required)
            {
                required.Add(parameter.Name);
            }
        }

        var schema = new Dictionary<string, object?>
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false,
        };

        if (required.Count > 0)
        {
            schema["required"] = required.ToArray();
        }

        return new ToolSchema(schema);
    }

    private static IEnumerable<IAgentTool> DefaultTools() =>
    [
        new SystemInfoTool(),
        new ProcessListTool(),
        new ProcessStopTool(),
        new ListDirectoryTool(),
        new ReadFileTool(),
        new WriteFileTool(),
        new SearchFilesTool(),
        new OpenPathTool(),
        new RunCommandTool(),
        new FetchUrlTool(),
        new OpenUrlTool(),
        new CurrentTimeTool(),
    ];
}
