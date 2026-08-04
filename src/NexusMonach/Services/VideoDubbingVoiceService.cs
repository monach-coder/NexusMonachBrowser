using System.Collections.Concurrent;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Independent speech lane for live video translation. It owns its queue,
/// STA thread and neural worker process, so Nexus commands never wait behind
/// translated phrases and capture/Whisper/OPUS can continue in parallel.
/// </summary>
public static class VideoDubbingVoiceService
{
    private static readonly BlockingCollection<DubbingVoiceItem> Queue = new(10);
    private static readonly object Sync = new();
    private static Thread? _thread;
    private static volatile bool _isSpeaking;
    private static bool _shutdown;
    private static int _stopGeneration;

    public static bool IsBusy => _isSpeaking || Queue.Count > 0;

    public static Task WarmUpAsync(CancellationToken cancellationToken = default) =>
        NeuralVoiceService.IsAvailable
            ? NeuralVoiceService.WarmUpAsync(NeuralVoiceLane.Dubbing, cancellationToken)
            : Task.CompletedTask;

    internal static Task<PreparedDubbingSpeech> PrepareAsync(string text, int rate,
        CancellationToken cancellationToken = default)
    {
        var safe = VoiceAssistantService.SanitizeForSpeech(text);
        if (safe.Length == 0)
            throw new ArgumentException("Текст локальной озвучки пуст.", nameof(text));
        return NeuralVoiceService.PrepareDubbingSpeechAsync(safe,
            SettingsService.Current.NeuralVoiceProfile, Math.Clamp(rate, -4, 4),
            cancellationToken);
    }

    internal static async Task<bool> SpeakPreparedAndWaitAsync(
        PreparedDubbingSpeech prepared, CancellationToken cancellationToken = default)
    {
        Initialize();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new DubbingVoiceItem(prepared.Text, 0,
            SettingsService.Current.NeuralVoiceProfile, completion,
            Volatile.Read(ref _stopGeneration), Cancel: false, Prepared: prepared);
        if (!Queue.TryAdd(item))
        {
            prepared.Dispose();
            return false;
        }
        try { return await completion.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
    }

    public static async Task<bool> SpeakAndWaitAsync(string text, int rate,
        CancellationToken cancellationToken = default)
    {
        var safe = VoiceAssistantService.SanitizeForSpeech(text);
        if (safe.Length == 0) return false;
        Initialize();
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new DubbingVoiceItem(safe, Math.Clamp(rate, -4, 4),
            SettingsService.Current.NeuralVoiceProfile, completion,
            Volatile.Read(ref _stopGeneration), Cancel: false, Prepared: null);
        if (!Queue.TryAdd(item)) return false;
        try { return await completion.Task.WaitAsync(cancellationToken); }
        catch (OperationCanceledException)
        {
            Stop();
            throw;
        }
    }

    public static void SuspendPlayback()
    {
        NeuralVoiceService.SuspendDubbingPlayback();
    }

    public static void ResumePlayback()
    {
        NeuralVoiceService.ResumeDubbingPlayback();
    }

    public static void Stop()
    {
        Initialize();
        Interlocked.Increment(ref _stopGeneration);
        while (Queue.TryTake(out var pending))
        {
            pending.Prepared?.Dispose();
            pending.Completion?.TrySetResult(false);
        }
        NeuralVoiceService.Stop(NeuralVoiceLane.Dubbing);
        Queue.TryAdd(new DubbingVoiceItem(string.Empty, 0,
            SettingsService.Current.NeuralVoiceProfile, null,
            Volatile.Read(ref _stopGeneration), Cancel: true, Prepared: null));
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            if (_shutdown) return;
            _shutdown = true;
        }
        Stop();
        Queue.CompleteAdding();
    }

    private static void Initialize()
    {
        lock (Sync)
        {
            if (_thread is { IsAlive: true }) return;
            if (_shutdown) return;
            _thread = new Thread(Run)
            {
                IsBackground = true,
                Name = "Nexus video dubbing synthesis"
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }
    }

    private static void Run()
    {
        try
        {
            foreach (var item in Queue.GetConsumingEnumerable())
            {
                if (item.Cancel)
                {
                    item.Completion?.TrySetResult(false);
                    continue;
                }
                if (_shutdown)
                {
                    item.Completion?.TrySetResult(false);
                    break;
                }
                try
                {
                    _isSpeaking = true;
                    if (item.Generation != Volatile.Read(ref _stopGeneration))
                    {
                        item.Completion?.TrySetResult(false);
                        continue;
                    }
                    var firstAttempt = item.Prepared is not null
                        ? NeuralVoiceService.TryPlayPreparedDubbingSpeech(item.Prepared)
                        : NeuralVoiceService.TrySpeak(item.Text, item.Profile, item.Rate,
                            NeuralVoiceLane.Dubbing);
                    if (firstAttempt)
                    {
                        item.Completion?.TrySetResult(true);
                        continue;
                    }
                    // A translated film must never alternate between Kseniya and
                    // an unrelated Windows voice. Try one clean worker restart;
                    // if the local voice still fails, report the phrase as failed.
                    var retried = item.Prepared is not null
                        ? NeuralVoiceService.TryPlayPreparedDubbingSpeech(item.Prepared)
                        : NeuralVoiceService.TrySpeak(item.Text, item.Profile, item.Rate,
                            NeuralVoiceLane.Dubbing);
                    item.Completion?.TrySetResult(retried);
                }
                catch (Exception ex)
                {
                    item.Completion?.TrySetException(ex);
                    CrashReportService.RecordNonFatal("video-translation",
                        "dubbing-synthesis", ex);
                }
                finally
                {
                    item.Prepared?.Dispose();
                    _isSpeaking = false;
                }
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("video-translation",
                "dubbing-thread", ex);
        }
        finally { _isSpeaking = false; }
    }

    private sealed record DubbingVoiceItem(string Text, int Rate,
        NeuralVoiceProfile Profile, TaskCompletionSource<bool>? Completion,
        int Generation, bool Cancel, PreparedDubbingSpeech? Prepared);
}
