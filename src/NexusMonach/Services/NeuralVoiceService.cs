using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NAudio.Wave;
using NexusMonach.Models;

namespace NexusMonach.Services;

public enum NeuralVoiceLane
{
    Assistant,
    Dubbing
}

internal sealed class PreparedDubbingSpeech : IDisposable
{
    private int _disposed;

    internal PreparedDubbingSpeech(string path, string text, TimeSpan duration)
    {
        Path = path;
        Text = text;
        Duration = duration;
    }

    internal string Path { get; }
    public string Text { get; }
    public TimeSpan Duration { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { File.Delete(Path); } catch { }
    }
}

/// <summary>
/// Persistent, network-free bridge to an installed Silero or Piper TTS worker.
/// Windows voice fallback is owned by the callers; Vosk is never selected.
/// </summary>
public static class NeuralVoiceService
{
    private static readonly LaneState AssistantLane = new("assistant");
    private static readonly LaneState DubbingLane = new("dubbing");
    private static readonly object PlaybackPrioritySync = new();
    private static readonly ManualResetEventSlim DubbingPlaybackAllowed = new(true);
    private static readonly TimeSpan DubbingColdSynthesisTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan WarmSynthesisTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan PlaybackTimeout = TimeSpan.FromMinutes(2);
    private static int _dubbingPauseDepth;
    private static bool _dubbingPausedByPriority;

    public static bool IsAvailable => AiModelCatalog.SileroVoiceReady || AiModelCatalog.PiperVoiceReady;
    public static string Status => IsAvailable
        ? AiModelCatalog.VoiceModelId + " · полностью локально"
        : "Nexus Local Voice · резервный женский голос Windows · полностью локально";
    internal static bool UsesIndependentLaneWorkers =>
        !ReferenceEquals(AssistantLane, DubbingLane) &&
        !ReferenceEquals(AssistantLane.WorkerSync, DubbingLane.WorkerSync) &&
        !ReferenceEquals(AssistantLane.RequestSync, DubbingLane.RequestSync);
    internal static TimeSpan DubbingColdStartBudget => DubbingColdSynthesisTimeout;
    internal static TimeSpan WarmRequestBudget => WarmSynthesisTimeout;
    internal static bool ShouldReportSynthesisFailure(int requestGeneration, int currentGeneration) =>
        requestGeneration == currentGeneration;

    public static async Task WarmUpAsync(NeuralVoiceLane lane,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(AiModelCatalog.MissingNeuralVoiceMessage);

        var state = StateFor(lane);
        if (state.IsReady && state.Worker is { HasExited: false }) return;
        var output = Path.Combine(Path.GetTempPath(),
            $"nexus-voice-warmup-{state.Name}-{Guid.NewGuid():N}.wav");
        try
        {
            // Холодный старт воркера падает пачками (гонка зеркалирования
            // модели, антивирус на свежераспакованном рантайме) — секунда
            // паузы и свежий процесс обычно снимают проблему.
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    await Task.Run(() => SynthesizeToFile("Нексус готов", NeuralVoiceProfile.Natasha,
                        0, lane, output, cancellationToken), cancellationToken);
                    break;
                }
                catch (WorkerExitedException) when (attempt < 3)
                {
                    StopWorker(state);
                    await Task.Delay(1000 + attempt * 500, cancellationToken);
                }
            }
        }
        catch
        {
            StopWorker(state);
            throw;
        }
        finally
        {
            try { File.Delete(output); } catch { }
        }
    }

    internal static async Task<PreparedDubbingSpeech> PrepareDubbingSpeechAsync(
        string text, NeuralVoiceProfile profile, int rate,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
            throw new InvalidOperationException(AiModelCatalog.MissingNeuralVoiceMessage);
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("Текст локальной озвучки пуст.", nameof(text));

        var output = Path.Combine(Path.GetTempPath(),
            $"nexus-voice-prepared-{Guid.NewGuid():N}.wav");
        try
        {
            await Task.Run(() => SynthesizeToFile(text, profile, rate,
                NeuralVoiceLane.Dubbing, output, cancellationToken), cancellationToken);
            using var reader = new WaveFileReader(output);
            return new PreparedDubbingSpeech(output, text, reader.TotalTime);
        }
        catch
        {
            try { File.Delete(output); } catch { }
            throw;
        }
    }

    internal static bool TryPlayPreparedDubbingSpeech(PreparedDubbingSpeech speech)
    {
        var state = DubbingLane;
        var generation = Volatile.Read(ref state.StopGeneration);
        return PlayWave(speech.Path, state, NeuralVoiceLane.Dubbing, generation);
    }

    public static bool TrySpeak(string text, NeuralVoiceProfile profile, int rate,
        NeuralVoiceLane lane = NeuralVoiceLane.Assistant)
    {
        if (!IsAvailable) return false;
        var state = StateFor(lane);
        var generation = Volatile.Read(ref state.StopGeneration);
        // A worker that exits before its first reply is usually a broken cold
        // start (inherited Python environment, unwritable temp directory,
        // half-extracted runtime). One retry with a fresh worker keeps startup
        // speech reliable without hiding persistent failures from Crash Vault.
        for (var attempt = 0; ; attempt++)
        {
            var output = Path.Combine(Path.GetTempPath(),
                $"nexus-voice-{state.Name}-{Guid.NewGuid():N}.wav");
            try
            {
                SynthesizeToFile(text, profile, rate, lane, output, CancellationToken.None);
                return PlayWave(output, state, lane, generation);
            }
            catch (Exception ex)
            {
                StopWorker(state);
                var stillCurrent =
                    ShouldReportSynthesisFailure(generation, Volatile.Read(ref state.StopGeneration));
                // Холодный старт воркера падает пачками: гонка зеркалирования
                // модели, антивирус на свежераспакованном рантайме. Мгновенный
                // повтор бьётся о ту же причину — даю секунду на отпускание.
                if (attempt < 3 && stillCurrent && ex is WorkerExitedException)
                {
                    Thread.Sleep(1000 + attempt * 500);
                    continue;
                }
                if (stillCurrent)
                    CrashReportService.RecordNonFatal("voice",
                        "neural-tts-" + state.Name, ex);
                return false;
            }
            finally
            {
                try { File.Delete(output); } catch { }
            }
        }
    }

    public static void Stop()
        => Stop(NeuralVoiceLane.Assistant);

    public static void Stop(NeuralVoiceLane lane)
    {
        var state = StateFor(lane);
        Interlocked.Increment(ref state.StopGeneration);
        lock (state.OutputSync)
            try { state.ActiveOutput?.Stop(); } catch { }
        // A worker can be blocked inside model inference and has no cooperative
        // cancellation API. Killing it is the only bounded, idempotent stop;
        // the next request starts a fresh worker on demand.
        StopWorker(state);
    }

    public static void Shutdown()
    {
        Stop(NeuralVoiceLane.Assistant);
        Stop(NeuralVoiceLane.Dubbing);
    }

    public static void SuspendDubbingPlayback()
    {
        lock (PlaybackPrioritySync)
        {
            _dubbingPauseDepth++;
            DubbingPlaybackAllowed.Reset();
            lock (DubbingLane.OutputSync)
                if (DubbingLane.ActiveOutput is { PlaybackState: PlaybackState.Playing } output)
                    try
                    {
                        output.Pause();
                        _dubbingPausedByPriority = true;
                    }
                    catch { }
        }
    }

    public static void ResumeDubbingPlayback()
    {
        lock (PlaybackPrioritySync)
        {
            if (_dubbingPauseDepth > 0) _dubbingPauseDepth--;
            if (_dubbingPauseDepth > 0) return;
            lock (DubbingLane.OutputSync)
                if (_dubbingPausedByPriority &&
                    DubbingLane.ActiveOutput is { PlaybackState: PlaybackState.Paused } output)
                    try { output.Play(); } catch { }
            _dubbingPausedByPriority = false;
            DubbingPlaybackAllowed.Set();
        }
    }

    private static Process EnsureWorker(LaneState state)
    {
        lock (state.WorkerSync)
        {
            var executable = AiModelCatalog.VoiceWorker
                ?? throw new FileNotFoundException(AiModelCatalog.MissingNeuralVoiceMessage);
            var model = AsciiSafeModelCache.EnsureAsciiSafePath(AiModelCatalog.VoiceModel);
            if (state.Worker is { HasExited: false } &&
                string.Equals(state.WorkerExecutable, executable, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(state.WorkerModel, model, StringComparison.OrdinalIgnoreCase))
                return state.Worker;
            if (state.Worker is not null)
            {
                try { if (!state.Worker.HasExited) state.Worker.Kill(true); } catch { }
                try { state.Worker.Dispose(); } catch { }
                state.Worker = null;
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
            // The worker embeds a Python runtime: PYTHONHOME/PYTHONPATH inherited
            // from a developer shell corrupt its imports, and a PyInstaller-style
            // pack needs an explicit TEMP/TMP when the parent shell omits them.
            start.Environment.Remove("PYTHONHOME");
            start.Environment.Remove("PYTHONPATH");
            if (string.IsNullOrWhiteSpace(start.Environment["TEMP"]))
                start.Environment["TEMP"] = Path.GetTempPath();
            if (string.IsNullOrWhiteSpace(start.Environment["TMP"]))
                start.Environment["TMP"] = Path.GetTempPath();
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(model);
            start.ArgumentList.Add("--stdio");
            start.Environment["NEXUS_VOICE_LANE"] = state.Name;
            var process = Process.Start(start)
                ?? throw new InvalidOperationException("Не удалось запустить локальный TTS.");
            state.Worker = process;
            state.WorkerExecutable = executable;
            state.WorkerModel = model;
            var workerGeneration = ++state.WorkerGeneration;
            state.WorkerStderrTail.Clear();
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!process.HasExited)
                    {
                        var line = await process.StandardError.ReadLineAsync();
                        if (line is null) break;
                        if (Volatile.Read(ref state.WorkerGeneration) != workerGeneration) break;
                        state.WorkerStderrTail.Enqueue(line);
                        while (state.WorkerStderrTail.Count > 8)
                            state.WorkerStderrTail.TryDequeue(out _);
                    }
                }
                catch { /* Stop/Shutdown may dispose the worker while stderr is drained. */ }
            });
            return process;
        }
    }

    private static void SynthesizeToFile(string text, NeuralVoiceProfile profile, int rate,
        NeuralVoiceLane lane, string output, CancellationToken cancellationToken)
    {
        var state = StateFor(lane);
        lock (state.RequestSync)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Мужской голос (Евгений) идёт через Piper: Silero-воркер не
            // умеет менять спикера (захардкожена Ксения), а пересборка
            // PyInstaller-воркера несовместима по torch.
            if (profile == NeuralVoiceProfile.Eugene && EugenePiperReady)
            {
                SynthesizeWithPiperCli(AiModelCatalog.PiperCli!, text, profile,
                    rate, lane, output, state, cancellationToken);
                state.IsReady = true;
                return;
            }
            if (!AiModelCatalog.SileroVoiceReady &&
                AiModelCatalog.PiperCli is { } piperCli && File.Exists(piperCli))
            {
                SynthesizeWithPiperCli(piperCli, text, profile, rate, lane, output,
                    state, cancellationToken);
                state.IsReady = true;
                return;
            }

            var worker = EnsureWorker(state);
            var requestId = Guid.NewGuid().ToString("N");
            // Silero понимает «+» перед гласной как ручное ударение. Маркер
            // ставится только здесь: SAPI-фолбэк и Piper произнесли бы «плюс».
            var spokenText = RussianStressDictionary.ApplyStress(text);
            var request = JsonSerializer.Serialize(new
            {
                id = requestId,
                text = spokenText,
                output,
                style = VoiceStyle(profile),
                speaker = SpeakerName(profile),
                rate = Math.Clamp(rate, -4, 4)
            });
            worker.StandardInput.WriteLine(request);
            worker.StandardInput.Flush();

            using var synthesisBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            synthesisBudget.CancelAfter(state.IsReady || lane == NeuralVoiceLane.Assistant
                ? WarmSynthesisTimeout
                : DubbingColdSynthesisTimeout);
            var replyLine = ReadReplyAsync(state, requestId, synthesisBudget.Token)
                .GetAwaiter().GetResult();
            using var reply = JsonDocument.Parse(replyLine);
            var root = reply.RootElement;
            if (!root.TryGetProperty("id", out var id) || id.GetString() != requestId ||
                !root.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
            {
                var error = root.TryGetProperty("error", out var value)
                    ? value.GetString()
                    : "неизвестная ошибка";
                throw new InvalidOperationException("Локальный TTS: " + error);
            }
            if (!File.Exists(output) || new FileInfo(output).Length < 1_000)
                throw new InvalidOperationException("Локальный TTS не создал корректный WAV-файл.");
            state.IsReady = true;
        }
    }

    private static void SynthesizeWithPiperCli(string executable, string text,
        NeuralVoiceProfile profile, int rate, NeuralVoiceLane lane, string output,
        LaneState state, CancellationToken cancellationToken)
    {
        var style = VoiceStyle(profile);
        var (noiseScale, noiseWidth, baseLength) = style switch
        {
            "calm" => (0.55, 0.65, 1.06),
            "expressive" => (0.80, 0.92, 0.94),
            _ => (0.667, 0.80, 1.00)
        };
        var speed = Math.Clamp(1.0 + Math.Clamp(rate, -4, 4) * 0.07, 0.72, 1.38);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(executable) ?? AppContext.BaseDirectory
        };
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(AsciiSafeModelCache.EnsureAsciiSafePath(AiModelCatalog.PiperVoiceModel));
        start.ArgumentList.Add("--output_file");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add("--noise_scale");
        start.ArgumentList.Add(noiseScale.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--noise_w");
        start.ArgumentList.Add(noiseWidth.ToString(CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--length_scale");
        start.ArgumentList.Add((baseLength / speed).ToString(CultureInfo.InvariantCulture));

        using var process = Process.Start(start)
            ?? throw new InvalidOperationException("Не удалось запустить локальный Piper.");
        lock (state.WorkerSync)
        {
            state.Worker = process;
            state.WorkerExecutable = executable;
            state.WorkerModel = AiModelCatalog.PiperVoiceModel;
        }
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            process.StandardInput.WriteLine(text);
            process.StandardInput.Close();
            using var synthesisBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            synthesisBudget.CancelAfter(state.IsReady || lane == NeuralVoiceLane.Assistant
                ? WarmSynthesisTimeout
                : DubbingColdSynthesisTimeout);
            process.WaitForExitAsync(synthesisBudget.Token).GetAwaiter().GetResult();
            var errorText = stderr.GetAwaiter().GetResult();
            _ = stdout.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Локальный Piper завершился с ошибкой: " +
                    errorText.Trim()[..Math.Min(errorText.Trim().Length, 500)]);
            if (!File.Exists(output) || new FileInfo(output).Length < 1_000)
                throw new InvalidOperationException("Локальный Piper не создал корректный WAV-файл.");
        }
        catch
        {
            try { if (!process.HasExited) process.Kill(true); } catch { }
            throw;
        }
        finally
        {
            lock (state.WorkerSync)
            {
                if (ReferenceEquals(state.Worker, process)) state.Worker = null;
                state.WorkerExecutable = null;
                state.WorkerModel = null;
            }
        }
    }

    private static bool PlayWave(string path, LaneState state, NeuralVoiceLane lane,
        int generation)
    {
        using var reader = new AudioFileReader(path);
        using var player = new WaveOutEvent();
        using var completed = new ManualResetEventSlim(false);
        StoppedEventArgs? stopped = null;
        player.PlaybackStopped += (_, args) =>
        {
            stopped = args;
            completed.Set();
        };
        try
        {
            player.Init(reader);
            if (lane == NeuralVoiceLane.Assistant)
            {
                SuspendDubbingPlayback();
                lock (state.OutputSync) state.ActiveOutput = player;
                player.Play();
            }
            else
            {
                while (true)
                {
                    while (!DubbingPlaybackAllowed.Wait(100))
                        if (generation != Volatile.Read(ref state.StopGeneration)) return false;
                    lock (PlaybackPrioritySync)
                    {
                        if (_dubbingPauseDepth > 0) continue;
                        lock (state.OutputSync) state.ActiveOutput = player;
                        player.Play();
                        break;
                    }
                }
            }
            if (!completed.Wait(PlaybackTimeout))
            {
                try { player.Stop(); } catch { }
                throw new TimeoutException("Воспроизведение локальной речи не завершилось вовремя.");
            }
            if (stopped?.Exception is not null) throw stopped.Exception;
            return generation == Volatile.Read(ref state.StopGeneration);
        }
        finally
        {
            lock (PlaybackPrioritySync)
            {
                lock (state.OutputSync)
                    if (ReferenceEquals(state.ActiveOutput, player)) state.ActiveOutput = null;
                if (lane == NeuralVoiceLane.Dubbing) _dubbingPausedByPriority = false;
            }
            if (lane == NeuralVoiceLane.Assistant) ResumeDubbingPlayback();
        }
    }

    private static async Task<string> ReadReplyAsync(LaneState state, string requestId,
        CancellationToken cancellationToken)
    {
        var worker = state.Worker
            ?? throw new InvalidOperationException("Локальный TTS-воркер больше не запущен.");
        for (var lineCount = 0; lineCount < 100; lineCount++)
        {
            var line = await worker.StandardOutput.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                // EOF means the worker process itself died; its stderr tail is
                // the only actionable diagnostic, so it belongs in the report.
                var tail = string.Join(" | ", state.WorkerStderrTail.ToArray());
                throw new WorkerExitedException(string.IsNullOrWhiteSpace(tail)
                    ? "Локальный процесс TTS завершился до ответа; stderr пуст."
                    : "Локальный процесс TTS завершился до ответа. Последние строки воркера: " + tail);
            }
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

    /// <summary>
    /// Готов ли мужской голос (Piper): exe и модель замализированы в
    /// ASCII-безопасный кэш, откуда Piper читает без падения на кириллице.
    /// </summary>
    private static bool EugenePiperReady
    {
        get
        {
            var cache = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NexusMonach", "VoiceCache", "piper");
            return File.Exists(Path.Combine(cache, "piper.exe")) &&
                   File.Exists(Path.Combine(cache, "ru_RU-denis-medium.onnx"));
        }
    }

    private static string VoiceStyle(NeuralVoiceProfile profile) => profile switch
    {
        NeuralVoiceProfile.Irina => "calm",
        NeuralVoiceProfile.Aurora => "expressive",
        _ => "natural"
    };

    private static string SpeakerName(NeuralVoiceProfile profile) => profile switch
    {
        NeuralVoiceProfile.Eugene => "eugene",
        NeuralVoiceProfile.Irina => "baya",
        NeuralVoiceProfile.Aurora => "xenia",
        _ => "kseniya"
    };

    private static LaneState StateFor(NeuralVoiceLane lane) =>
        lane == NeuralVoiceLane.Dubbing ? DubbingLane : AssistantLane;

    private static void StopWorker(LaneState state)
    {
        Process? worker;
        lock (state.WorkerSync)
        {
            worker = state.Worker;
            state.Worker = null;
            state.WorkerExecutable = null;
            state.WorkerModel = null;
            state.IsReady = false;
            state.WorkerGeneration++;
            state.WorkerStderrTail.Clear();
        }
        if (worker is null) return;
        try { if (!worker.HasExited) worker.Kill(true); } catch { }
        try { worker.Dispose(); } catch { }
    }

    /// <summary>Thrown when the TTS worker process dies before answering a request.</summary>
    private sealed class WorkerExitedException(string message) : InvalidOperationException(message);

    private sealed class LaneState(string name)
    {
        public string Name { get; } = name;
        public object WorkerSync { get; } = new();
        public object RequestSync { get; } = new();
        public object OutputSync { get; } = new();
        public Process? Worker { get; set; }
        public string? WorkerExecutable { get; set; }
        public string? WorkerModel { get; set; }
        public bool IsReady { get; set; }
        public WaveOutEvent? ActiveOutput { get; set; }
        public int StopGeneration;
        public int WorkerGeneration;
        public ConcurrentQueue<string> WorkerStderrTail { get; } = new();
    }
}
