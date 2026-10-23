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
        var settings = new AssistantSettings();
        settings.WorkspacePath = AppPaths.DefaultWorkspace;
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

            await using var stream = File.OpenRead(_file);
            var loaded = await JsonSerializer
                .DeserializeAsync<AssistantSettings>(stream, AssistantJson.Options, cancellationToken)
                .ConfigureAwait(false);

            _settings = loaded ?? CreateDefault();
            Normalize(_settings);
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
            await using (var stream = File.Create(temp))
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
        if (string.IsNullOrWhiteSpace(settings.WorkspacePath))
        {
            settings.WorkspacePath = AppPaths.DefaultWorkspace;
        }

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
        settings.DisabledTools = settings.DisabledTools
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }
}
