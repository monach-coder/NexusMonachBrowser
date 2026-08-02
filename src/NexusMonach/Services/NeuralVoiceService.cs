using System.Diagnostics;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Persistent, network-free bridge to the Vosk TTS worker. The model is loaded
/// once and generated WAV files live only in the OS temporary directory.
/// </summary>
public static class NeuralVoiceService
{
    private static readonly object WorkerSync = new();
    private static readonly object OutputSync = new();
    private static readonly object RequestSync = new();
    private static readonly TimeSpan SynthesisTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PlaybackTimeout = TimeSpan.FromMinutes(2);
    private static Process? _worker;
    private static WaveOutEvent? _activeOutput;

    public static bool IsAvailable => AiModelCatalog.NeuralVoiceReady;
    public static string Status => IsAvailable
        ? "Nexus Neural Voice · Vosk TTS · полностью локально"
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
                    throw new InvalidOperationException("Vosk TTS: " + error);
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
            if (_worker is { HasExited: false }) return _worker;
            var executable = AiModelCatalog.VoiceWorker
                ?? throw new FileNotFoundException(AiModelCatalog.MissingNeuralVoiceMessage);
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
            start.ArgumentList.Add(AiModelCatalog.VoiceModelRoot);
            start.ArgumentList.Add("--stdio");
            var process = Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить локальный Vosk TTS.");
            _worker = process;
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
                throw new InvalidOperationException("Локальный процесс Vosk TTS не вернул ответ.");
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
        throw new InvalidOperationException("Vosk TTS вернул слишком много служебных строк без ответа.");
    }

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
        }
        if (worker is null) return;
        try { if (!worker.HasExited) worker.Kill(true); } catch { }
        try { worker.Dispose(); } catch { }
    }
}
