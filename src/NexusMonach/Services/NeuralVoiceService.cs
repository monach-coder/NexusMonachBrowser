using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Persistent, network-free bridge to the best installed TTS worker. Piper HD
/// is preferred; the verified Vosk voice pack and Windows voice remain fallbacks.
/// </summary>
public static class NeuralVoiceService
{
    private static readonly object WorkerSync = new();
    private static readonly object OutputSync = new();
    private static readonly object RequestSync = new();
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PlaybackTimeout = TimeSpan.FromMinutes(2);
    private static Process? _worker;
    private static string? _workerExecutable;
    private static string? _workerModel;
    private static WaveOutEvent? _activeOutput;

    public static bool IsAvailable => AiModelCatalog.NeuralVoiceReady;
    public static string Status => IsAvailable
        ? AiModelCatalog.VoiceModelId + " · полностью локально"
        : AiModelCatalog.MissingNeuralVoiceMessage;

    public static bool TrySpeak(string text, NeuralVoiceProfile profile, int rate)
    {
        if (!IsAvailable) return false;
        var output = Path.Combine(Path.GetTempPath(), $"nexus-voice-{Guid.NewGuid():N}.wav");
        try
        {
            lock (RequestSync)
            {
                var worker = EnsureWorker();
                var requestId = Guid.NewGuid().ToString("N");
                var request = JsonSerializer.Serialize(new
                {
                    id = requestId,
                    text,
                    output,
                    style = VoiceStyle(profile),
                    speaker = SpeakerId(profile),
                    rate = Math.Clamp(rate, -4, 4)
                });
                worker.StandardInput.WriteLine(request);
                worker.StandardInput.Flush();

                using var synthesisBudget = new CancellationTokenSource(SynthesisTimeout);
                var replyLine = ReadReplyAsync(worker, requestId, synthesisBudget.Token)
                    .GetAwaiter().GetResult();
                using var reply = JsonDocument.Parse(replyLine);
                var root = reply.RootElement;
                if (!root.TryGetProperty("id", out var id) || id.GetString() != requestId ||
                    !root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
                {
                    var error = root.TryGetProperty("error", out var value) ? value.GetString() : "неизвестная ошибка";
                    throw new InvalidOperationException("Локальный TTS: " + error);
                }

                PlayWave(output);
            }
            return true;
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("voice", "neural-tts", ex);
            StopWorker();
            return false;
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }

    public static void Stop()
    {
        lock (OutputSync)
            try { _activeOutput?.Stop(); } catch { }
        // A worker can be blocked inside model inference and has no cooperative
        // cancellation API. Killing it is the only bounded, idempotent stop;
        // the next request starts a fresh worker on demand.
        StopWorker();
    }

    public static void Shutdown()
    {
        Stop();
        StopWorker();
    }

    private static Process EnsureWorker()
    {
        lock (WorkerSync)
        {
            var executable = AiModelCatalog.VoiceWorker
                ?? throw new FileNotFoundException(AiModelCatalog.MissingNeuralVoiceMessage);
            var model = AiModelCatalog.VoiceModel;
            if (_worker is { HasExited: false } &&
                string.Equals(_workerExecutable, executable, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(_workerModel, model, StringComparison.OrdinalIgnoreCase))
                return _worker;
            if (_worker is not null)
            {
                try { if (!_worker.HasExited) _worker.Kill(true); } catch { }
                try { _worker.Dispose(); } catch { }
                _worker = null;
            }
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(model);
            start.ArgumentList.Add("--stdio");
            var process = Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить локальный TTS.");
            _worker = process;
            _workerExecutable = executable;
            _workerModel = model;
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.HasExited)
                    {
                        var line = await process.StandardError.ReadLineAsync();
                        if (line is null) break;
                    }
                }
                catch { /* Stop/Shutdown may dispose the worker while stderr is drained. */ }
            });
            return process;
        }
    }

    private static void PlayWave(string path)
    {
        using var reader = new AudioFileReader(path);
        using var player = new WaveOutEvent();
        using var completed = new ManualResetEventSlim(false);
        player.PlaybackStopped += (_, _) => completed.Set();
        lock (OutputSync) _activeOutput = player;
        try
        {
            player.Init(reader);
            player.Play();
            if (!completed.Wait(PlaybackTimeout))
            {
                try { player.Stop(); } catch { }
                throw new TimeoutException("Воспроизведение локальной речи не завершилось вовремя.");
            }
        }
        finally
        {
            lock (OutputSync)
                if (ReferenceEquals(_activeOutput, player)) _activeOutput = null;
        }
    }

    private static async Task<string> ReadReplyAsync(Process worker, string requestId,
        CancellationToken cancellationToken)
    {
        for (var lineCount = 0; lineCount < 100; lineCount++)
        {
            var line = await worker.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
                throw new InvalidOperationException("Локальный процесс TTS не вернул ответ.");
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("id", out var id) && id.GetString() == requestId)
                    return line;
            }
            catch (JsonException)
            {
                // A native dependency may write a banner directly to stdout.
                // Only the matching structured reply belongs to the protocol.
            }
        }
        throw new InvalidOperationException("Локальный TTS вернул слишком много служебных строк без ответа.");
    }

    private static string VoiceStyle(NeuralVoiceProfile profile) => profile switch
    {
        NeuralVoiceProfile.Irina => "calm",
        NeuralVoiceProfile.Aurora => "expressive",
        _ => "natural"
    };

    private static int SpeakerId(NeuralVoiceProfile profile) => profile switch
    {
        NeuralVoiceProfile.Irina => 0,
        NeuralVoiceProfile.Natasha => 1,
        _ => 2
    };

    private static void StopWorker()
    {
        Process? worker;
        lock (WorkerSync)
        {
            worker = _worker;
            _worker = null;
            _workerExecutable = null;
            _workerModel = null;
        }
        if (worker is null) return;
        try { if (!worker.HasExited) worker.Kill(true); } catch { }
        try { worker.Dispose(); } catch { }
    }
}
