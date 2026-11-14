namespace AvaloniaAssistant.Abstractions;

/// <summary>
/// 专家包：本地的「专家市场」等价物。导出成 JSON 文件、从 JSON 文件导入，
/// 可以在几台机器之间搬自己配的专家，不需要云端账号或市场服务。
/// 这里只放纯数据（Abstractions 不引用任何其它层），JSON 读写由 UI 层用
/// AssistantJson.Options 完成，AOT 下由 AotJson 登记 Pack 类型。
/// </summary>
public static class ExpertPack
{
    public const string Kind = "avalonia-assistant-experts";

    public sealed class Pack
    {
        public string Kind { get; set; } = ExpertPack.Kind;

        public int Version { get; set; } = 1;

        public List<ExpertPreset> Experts { get; set; } = [];
    }

    /// <summary>只收自定义档位：内置档位由 ExpertCatalog 保证，不随包重复、也不允许覆盖。</summary>
    public static Pack Create(IEnumerable<ExpertPreset> experts) => new()
    {
        Experts = experts
            .Where(item => !item.BuiltIn && !string.IsNullOrWhiteSpace(item.Directive))
            .Select(item => new ExpertPreset
            {
                Name = item.Name,
                Description = item.Description,
                Directive = item.Directive,
                BuiltIn = false,
                Enabled = true,
            })
            .ToList(),
    };

    /// <summary>
    /// 校验一个包并转成可入库的档位。不是本应用的包、字段缺失、重复的都过滤掉，
    /// 不给坏文件任何机会写坏存档；id 一律重发，避免和已有专家撞车。
    /// </summary>
    public static IReadOnlyList<ExpertPreset> Normalize(Pack? pack)
    {
        if (pack is null || !string.Equals(pack.Kind, Kind, StringComparison.Ordinal))
        {
            return [];
        }

        return pack.Experts
            .Where(item => !string.IsNullOrWhiteSpace(item.Name)
                           && !string.IsNullOrWhiteSpace(item.Directive))
            .Select(item => new ExpertPreset
            {
                Id = Guid.NewGuid().ToString("n"),
                Name = item.Name.Trim(),
                Description = item.Description,
                Directive = item.Directive.Trim(),
                BuiltIn = false,
                Enabled = true,
            })
            .ToList();
    }
}
