using System.Net.Http.Headers;
using System.Text.Json;
using AvaloniaAssistant.Abstractions;
using NAudio.Wave;

namespace AvaloniaAssistant.Speech;

/// <summary>
/// NAudio 录音 + OpenAI 兼容的 /audio/transcriptions 转写。
/// 相比原项目的本地 Whisper，这里不捆绑模型文件；端点不支持时给出明确错误。
/// </summary>
public sealed class NAudioVoiceInput : IVoiceInput, IDisposable
{
    private static readonly HttpClient Client = new() { Timeout = TimeSpan.FromSeconds(90) };

    private readonly ISettingsProvider _settings;
    private readonly object _writerGate = new();
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private string? _file;
    private bool _disposed;

    public NAudioVoiceInput(ISettingsProvider settings)
    {
        _settings = settings;
    }

    public bool IsRecording { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (IsRecording)
        {
            return Task.CompletedTask;
        }

        _file = Path.Combine(Path.GetTempPath(), $"ava-voice-{Guid.NewGuid():n}.wav");
        var format = new WaveFormat(16_000, 1);

        _writer = new WaveFileWriter(_file, format);
        _waveIn = new WaveInEvent
        {
            WaveFormat = format,
            BufferMilliseconds = 80,
            NumberOfBuffers = 3,
        };

        _waveIn.DataAvailable += (_, args) =>
        {
            lock (_writerGate)
            {
                try
                {
                    _writer?.Write(args.Buffer, 0, args.BytesRecorded);
                }
                catch (ObjectDisposedException)
                {
                    // 停止录音时的竞态，忽略。
                }
            }
        };

        try
        {
            _waveIn.StartRecording();
        }
        catch
        {
            lock (_writerGate)
            {
                _writer?.Dispose();
                _writer = null;
            }

            _waveIn.Dispose();
            _waveIn = null;
            try
            {
                File.Delete(_file);
            }
            catch (IOException)
            {
                // 忽略。
            }

            throw;
        }

        IsRecording = true;

        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public async Task<string> StopAndTranscribeAsync(CancellationToken cancellationToken = default)
    {
        if (!IsRecording)
        {
            return string.Empty;
        }

        StopRecording();

        var file = _file;
        _file = null;
        if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
        {
            throw new InvalidOperationException("没有录到音频。");
        }

        try
        {
            var info = new FileInfo(file);
            if (info.Length < 2_000)
            {
                throw new InvalidOperationException("录音太短，请按住说话后再停止。");
            }

            return await TranscribeAsync(file, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 临时文件清理失败不影响结果。
            }
        }
    }

    private async Task<string> TranscribeAsync(string file, CancellationToken cancellationToken)
    {
        var settings = _settings.Current;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException("还没有配置 API Key。");
        }

        var endpoint = settings.BaseUrl.TrimEnd('/') + "/audio/transcriptions";
        using var form = new MultipartFormDataContent();
        await using var audio = File.OpenRead(file);
        var audioContent = new StreamContent(audio);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        form.Add(audioContent, "file", "audio.wav");
        form.Add(new StringContent(string.IsNullOrWhiteSpace(settings.SpeechModel) ? "whisper-1" : settings.SpeechModel), "model");

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = form };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);

        using var response = await Client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var snippet = body.Length > 300 ? body[..300] : body;
            throw new InvalidOperationException(
                $"语音转写失败（{(int)response.StatusCode}）。当前端点可能不支持 /audio/transcriptions。{snippet}");
        }

        using var document = JsonDocument.Parse(body);
        if (document.RootElement.TryGetProperty("text", out var text))
        {
            return text.GetString()?.Trim() ?? string.Empty;
        }

        throw new InvalidOperationException("语音转写返回了无法识别的结果。");
    }

    public void Cancel()
    {
        if (!IsRecording)
        {
            return;
        }

        StopRecording();
        var file = _file;
        _file = null;
        if (!string.IsNullOrWhiteSpace(file))
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // 忽略。
            }
        }
    }

    private void StopRecording()
    {
        IsRecording = false;

        try
        {
            _waveIn?.StopRecording();
        }
        catch (Exception)
        {
            // 设备可能已被拔出。
        }

        _waveIn?.Dispose();
        _waveIn = null;

        lock (_writerGate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (IsRecording)
        {
            StopRecording();
        }
    }
}
