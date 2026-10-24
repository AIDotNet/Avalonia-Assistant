using System.Text.Json;
using System.Text.Json.Nodes;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Storage;

/// <summary>settings.json 读写。写入走临时文件 + 替换，避免半截文件。</summary>
public sealed class JsonSettingsProvider : ISettingsProvider
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _file;
    private AssistantSettings _settings = CreateDefault();

    public JsonSettingsProvider(string? file = null)
    {
        _file = file ?? AppPaths.SettingsFile;
    }

    public AssistantSettings Current => _settings;

    public event EventHandler? Changed;

    public static AssistantSettings CreateDefault()
    {
        // 首装默认是「普通任务」：工作区得用户自己挑一次。
        // 早先这里塞了 ~/AvaloniaAssistant/Workspace，等于替人做了决定——
        // 一打开就在某个目录里读写文件，而这个目录人根本没选过。
        var settings = new AssistantSettings();

        // 至少摆一套空配置：界面上要能看到「当前模型」，而不是一片空白。
        settings.NormalizeModels();
        return settings;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!File.Exists(_file))
            {
                _settings = CreateDefault();
                return;
            }

            var text = await File.ReadAllTextAsync(_file, cancellationToken).ConfigureAwait(false);
            _settings = LoadFromJson(text);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _settings = CreateDefault();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// 解析一份 settings.json。
    /// <para>
    /// 旧格式是「一套扁平配置」（provider / apiKey / baseUrl / model / protocol），
    /// 这里把它搬成 models 里的第一套，并设为当前模型；新格式直接用。
    /// 迁移放在读取这一层，是因为那几个键和 <see cref="AssistantSettings"/> 上同名的
    /// 只读代理属性撞名，走 <c>JsonExtensionData</c> 会被直接吞掉。
    /// </para>
    /// </summary>
    public static AssistantSettings LoadFromJson(string json)
    {
        AssistantSettings? settings = null;

        if (JsonNode.Parse(json) is JsonObject root)
        {
            var hasModels = root.ContainsKey("models");
            settings = root.Deserialize<AssistantSettings>(AssistantJson.Options);

            if (settings is not null && !hasModels)
            {
                var profile = ModelProfile.FromLegacy(
                    ReadString(root, "provider"),
                    ReadString(root, "baseUrl"),
                    ReadString(root, "apiKey"),
                    ReadString(root, "model"),
                    ReadString(root, "protocol"));

                if (profile.Model.Length > 0 || profile.BaseUrl.Length > 0 || profile.ApiKey.Length > 0)
                {
                    settings.Models.Insert(0, profile);
                    settings.ActiveModelId = profile.Id;
                }
            }
        }

        settings ??= new AssistantSettings();
        settings.NormalizeModels();
        return settings;
    }

    private static string ReadString(JsonObject root, string key)
        => root.TryGetPropertyValue(key, out var node) && node is JsonValue value
           && value.TryGetValue<string>(out var text)
            ? (text ?? string.Empty).Trim()
            : string.Empty;

    public async Task SaveAsync(AssistantSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Normalize(settings);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            var temp = _file + ".tmp";

            // await using 的释放也要 ConfigureAwait(false)：关窗时 UI 线程会同步等这次保存，
            // 释放续体如果再排回 UI 线程，就是死锁。
            var stream = File.Create(temp);
            await using (stream.ConfigureAwait(false))
            {
                await JsonSerializer
                    .SerializeAsync(stream, settings, AssistantJson.Options, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temp, _file, overwrite: true);
            _settings = settings.Clone();
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Normalize(AssistantSettings settings)
    {
        // 工作区允许为空：用户把「空间」里的记录删干净后，任务就是普通对话。
        // 空值不能再回落到默认工作区，否则删了下次启动又冒出来。
        //
        // 模型配置：旧文件会被搬成一套模型；新文件在这里补齐空模型名 / 空地址、
        // 去重 id、校正「当前模型」指向，保证运行时不面对半套配置。
        settings.NormalizeModels();

        settings.MaxTokens = Math.Clamp(settings.MaxTokens, 256, 128_000);
        settings.Temperature = Math.Clamp(settings.Temperature, 0, 2);
        settings.RecentWorkspaces = settings.RecentWorkspaces
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();
        settings.CollapsedWorkspaces = (settings.CollapsedWorkspaces ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        settings.DisabledTools = settings.DisabledTools
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // 窗口/侧栏/缩放：旧配置里没有这些字段，读出来是 0，回落到默认值再夹紧。
        settings.StartupPage = string.Equals(settings.StartupPage, "last", StringComparison.OrdinalIgnoreCase)
            ? "last"
            : "home";
        settings.SidebarWidth = Math.Clamp(settings.SidebarWidth <= 0 ? 268 : settings.SidebarWidth, 220, 380);
        settings.FontScale = Math.Clamp(settings.FontScale <= 0 ? 1 : settings.FontScale, 0.9, 1.25);
        settings.WindowWidth = Math.Clamp(settings.WindowWidth <= 0 ? 1280 : settings.WindowWidth, 960, 10_000);
        settings.WindowHeight = Math.Clamp(settings.WindowHeight <= 0 ? 820 : settings.WindowHeight, 640, 10_000);
    }
}
