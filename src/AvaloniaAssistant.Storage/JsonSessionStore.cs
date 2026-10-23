using System.Text.Json;
using AvaloniaAssistant.Abstractions;

namespace AvaloniaAssistant.Storage;

/// <summary>每个会话一个 JSON 文件，目录可直接备份/同步。</summary>
public sealed class JsonSessionStore : ISessionStore
{
    private readonly string _directory;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public JsonSessionStore(string? directory = null)
    {
        _directory = directory ?? AppPaths.Sessions;
    }

    public async Task<IReadOnlyList<ChatSession>> LoadAllAsync(CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(_directory))
        {
            return [];
        }

        var sessions = new List<ChatSession>();
        foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var stream = File.OpenRead(file);
                var session = await JsonSerializer
                    .DeserializeAsync<ChatSession>(stream, AssistantJson.Options, cancellationToken)
                    .ConfigureAwait(false);

                if (session is not null && !string.IsNullOrWhiteSpace(session.Id))
                {
                    sessions.Add(session);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // 损坏的会话文件不应阻止应用启动。
            }
        }

        return sessions
            .OrderByDescending(session => session.UpdatedAt)
            .ToList();
    }

    public async Task SaveAsync(ChatSession session, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        Directory.CreateDirectory(_directory);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var file = Path.Combine(_directory, session.Id + ".json");
            var temp = file + ".tmp";
            await using (var stream = File.Create(temp))
            {
                await JsonSerializer
                    .SerializeAsync(stream, session, AssistantJson.Options, cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temp, file, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public Task DeleteAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        var file = Path.Combine(_directory, sessionId + ".json");
        if (File.Exists(file))
        {
            File.Delete(file);
        }

        return Task.CompletedTask;
    }
}
