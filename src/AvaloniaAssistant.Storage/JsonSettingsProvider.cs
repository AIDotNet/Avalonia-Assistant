using System.Text.Json;
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
        return new AssistantSettings();
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

            // 释放同样不能回到被阻塞的 UI 线程：启动时这里是同步等待加载完成。
            var stream = File.OpenRead(_file);
            await using (stream.ConfigureAwait(false))
            {
                var loaded = await JsonSerializer
                    .DeserializeAsync<AssistantSettings>(stream, AssistantJson.Options, cancellationToken)
                    .ConfigureAwait(false);

                _settings = loaded ?? CreateDefault();
                Normalize(_settings);
            }
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
        if (string.IsNullOrWhiteSpace(settings.Model))
        {
            settings.Model = ProviderPreset.For(settings.Provider).Model;
        }

        if (string.IsNullOrWhiteSpace(settings.BaseUrl))
        {
            settings.BaseUrl = ProviderPreset.For(settings.Provider).BaseUrl;
        }

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
