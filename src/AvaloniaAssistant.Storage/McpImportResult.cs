namespace AvaloniaAssistant.Storage;

/// <summary>
/// 一次 JSON 导入的结果：新写进去的、覆盖掉旧条目的、因为同名而跳过的。
/// 三个列表都按粘贴顺序排，方便直接拼成一句提示。
/// </summary>
public sealed record McpImportResult(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Replaced,
    IReadOnlyList<string> Skipped)
{
    /// <summary>真正写进 mcp.json 的条数（新增 + 覆盖）。</summary>
    public int Written => Added.Count + Replaced.Count;
}
