using System.Text.Json;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Storage;

/// <summary>
/// 定时任务 / 资料库 / 专家的本地存档：数据目录下一个 workbench.json。
/// 和 settings.json / sessions 分开，删掉这个文件只影响这几项配置。
/// </summary>
public sealed class JsonWorkbenchStore
{
    private readonly string _file;

    public JsonWorkbenchStore(string? root = null)
        => _file = Path.Combine(root ?? AppPaths.Root, "workbench.json");

    public WorkbenchData Data { get; private set; } = new();

    public string FilePath => _file;

    public async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_file))
            {
                Data = new WorkbenchData();
                return;
            }

            var json = await File.ReadAllTextAsync(_file).ConfigureAwait(false);
            Data = JsonSerializer.Deserialize<WorkbenchData>(json, AssistantJson.Options) ?? new WorkbenchData();
            Data.ScheduledTasks ??= [];
            Data.RunRecords ??= [];
            Data.LibraryItems ??= [];
            Data.Experts ??= [];
            Data.Projects ??= [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // 存档坏了不该让整个应用起不来：留一份空的，用户重新配一次即可。
            Data = new WorkbenchData();
        }
    }

    public async Task SaveAsync()
    {
        var directory = Path.GetDirectoryName(_file);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(Data, AssistantJson.Options);
        await File.WriteAllTextAsync(_file, json).ConfigureAwait(false);
    }
}
