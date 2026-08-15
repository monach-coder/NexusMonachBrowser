using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Converts finalized translations to local WAV files ahead of playback. The
/// speaker starts only after a small mode-specific reserve is ready and keeps a
/// bounded refill margin while capture, Whisper and OPUS continue independently.
/// </summary>
internal sealed class VideoDubbingBuffer : IAsyncDisposable
{
    private readonly Channel<VideoSpeechTranslationText> _translations;
    private readonly Channel<PreparedItem> _prepared;
    private readonly VideoDubbingModeProfile _profile;
    private readonly VideoDubbingDiagnosticLog _diagnostics;
    private readonly Func<Task>? _beforeSpeaking;
    private readonly Func<Task>? _afterSpeaking;
    private readonly CancellationTokenSource _stop;
    private readonly ReadyAudioReserve _reserve = new();
    private readonly Task _preparer;
    private readonly Task _speaker;
    private int _completed;

    public VideoDubbingBuffer(VideoDubbingModeProfile profile,
        VideoDubbingDiagnosticLog diagnostics, CancellationToken cancellationToken,
        Func<Task>? beforeSpeaking = null, Func<Task>? afterSpeaking = null)
    {
        _profile = profile;
        _diagnostics = diagnostics;
        _beforeSpeaking = beforeSpeaking;
        _afterSpeaking = afterSpeaking;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _translations = Channel.CreateBounded<VideoSpeechTranslationText>(
            new BoundedChannelOptions(profile.PreparedQueueCapacity * 2)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
        _prepared = Channel.CreateBounded<PreparedItem>(
            new BoundedChannelOptions(profile.PreparedQueueCapacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = true
            });
        _preparer = PrepareLoopAsync();
        _speaker = PlaybackLoopAsync();
    }

    public ValueTask QueueAsync(VideoSpeechTranslationText translation) =>
        _translations.Writer.WriteAsync(translation, _stop.Token);

    public void Stop() => _stop.Cancel();

    public async Task CompleteAsync()
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0)
            _translations.Writer.TryComplete();
        try { await _preparer.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
        try { await _speaker.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
    }

    private async Task PrepareLoopAsync()
    {
        try
        {
            await foreach (var translation in _translations.Reader.ReadAllAsync(_stop.Token))
            {
                var ttsText = VideoDubbingPolicy.PrepareTtsText(translation.RussianText, _profile);
                if (ttsText.Length == 0) continue;
                PreparedDubbingSpeech? speech = null;
                var addedToReserve = false;
                try
                {
                    speech = await VideoDubbingVoiceService.PrepareAsync(ttsText,
                        VideoDubbingPolicy.SelectSpeechRate(ttsText.Length), _stop.Token)
                        .ConfigureAwait(false);
                    if (!VideoDubbingPolicy.IsPreparedAudioAcceptable(speech.Duration, _profile))
                    {
                        speech.Dispose();
                        ttsText = VideoDubbingPolicy.PrepareTtsText(ttsText, _profile,
                            Math.Max(40, _profile.MaximumTtsCharacters / 2));
                        speech = await VideoDubbingVoiceService.PrepareAsync(ttsText,
                            VideoDubbingPolicy.SelectSpeechRate(ttsText.Length), _stop.Token)
                            .ConfigureAwait(false);
                    }
                    if (!VideoDubbingPolicy.IsPreparedAudioAcceptable(speech.Duration, _profile))
                    {
                        speech.Dispose();
                        speech = null;
                        continue;
                    }
                    _reserve.Add(speech.Duration);
                    addedToReserve = true;
                    var item = new PreparedItem(translation, ttsText, speech);
                    await _diagnostics.WriteAsync("prepared", item,
                        _reserve.Snapshot()).ConfigureAwait(false);
                    await _prepared.Writer.WriteAsync(item, _stop.Token).ConfigureAwait(false);
                    speech = null;
                    addedToReserve = false;
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    CrashReportService.RecordNonFatal("video-translation",
                        "prepare-local-tts", ex);
                }
                finally
                {
                    if (speech is not null && addedToReserve)
                        _reserve.Remove(speech.Duration);
                    speech?.Dispose();
                }
            }
        }
        finally { _prepared.Writer.TryComplete(); }
    }

    private async Task PlaybackLoopAsync()
    {
        var ready = new Queue<PreparedItem>();
        try
        {
            await FillStartupReserveAsync(ready).ConfigureAwait(false);
            while (!_stop.IsCancellationRequested)
            {
                if (ready.Count == 0)
                {
                    PreparedItem next;
                    try { next = await _prepared.Reader.ReadAsync(_stop.Token).ConfigureAwait(false); }
                    catch (ChannelClosedException) { break; }
                    ready.Enqueue(next);
                }

                while (_prepared.Reader.TryRead(out var buffered)) ready.Enqueue(buffered);
                if (ready.Count == 1 && !_prepared.Reader.Completion.IsCompleted)
                    await WaitForRefillAsync(ready).ConfigureAwait(false);

                var item = ready.Dequeue();
                _reserve.Remove(item.Speech.Duration);
                await _diagnostics.WriteAsync("playback-start", item,
                    _reserve.Snapshot()).ConfigureAwait(false);
                var handedToVoiceQueue = false;
                try
                {
                    if (_beforeSpeaking is not null)
                        await _beforeSpeaking().ConfigureAwait(false);
                    handedToVoiceQueue = true;
                    var spoken = await VideoDubbingVoiceService.SpeakPreparedAndWaitAsync(
                        item.Speech, _stop.Token).ConfigureAwait(false);
                    if (!spoken)
                        throw new InvalidOperationException(
                            "Локальный голос не смог воспроизвести подготовленную реплику.");
                    await _diagnostics.WriteAsync("playback-complete", item,
                        _reserve.Snapshot()).ConfigureAwait(false);
                }
                finally
                {
                    if (!handedToVoiceQueue) item.Speech.Dispose();
                    if (_afterSpeaking is not null)
                        await _afterSpeaking().ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _stop.Cancel();
            CrashReportService.RecordNonFatal("video-translation",
                "prepared-playback", ex);
        }
        finally
        {
            while (ready.TryDequeue(out var pending))
            {
                _reserve.Remove(pending.Speech.Duration);
                pending.Speech.Dispose();
            }
            while (_prepared.Reader.TryRead(out var pending))
            {
                _reserve.Remove(pending.Speech.Duration);
                pending.Speech.Dispose();
            }
        }
    }

    private async Task FillStartupReserveAsync(Queue<PreparedItem> ready)
    {
        var startedAt = DateTimeOffset.MinValue;
        while (!_stop.IsCancellationRequested &&
               (ready.Count < _profile.StartupPreparedPhrases ||
                ready.Sum(item => item.Speech.Duration.TotalSeconds) < _profile.StartupPreparedSeconds))
        {
            var remaining = startedAt == DateTimeOffset.MinValue
                ? Timeout.InfiniteTimeSpan
                : TimeSpan.FromMilliseconds(_profile.StartupMaximumWaitMilliseconds) -
                  (DateTimeOffset.UtcNow - startedAt);
            if (remaining != Timeout.InfiniteTimeSpan && remaining <= TimeSpan.Zero) break;
            try
            {
                var read = _prepared.Reader.ReadAsync(_stop.Token).AsTask();
                var item = remaining == Timeout.InfiniteTimeSpan
                    ? await read.ConfigureAwait(false)
                    : await read.WaitAsync(remaining, _stop.Token).ConfigureAwait(false);
                if (startedAt == DateTimeOffset.MinValue) startedAt = DateTimeOffset.UtcNow;
                ready.Enqueue(item);
            }
            catch (TimeoutException) { break; }
            catch (ChannelClosedException) { break; }
        }
    }

    private async Task WaitForRefillAsync(Queue<PreparedItem> ready)
    {
        try
        {
            if (!await _prepared.Reader.WaitToReadAsync(_stop.Token).AsTask().WaitAsync(
                    TimeSpan.FromMilliseconds(_profile.RefillWaitMilliseconds), _stop.Token)
                .ConfigureAwait(false))
                return;
            while (_prepared.Reader.TryRead(out var item)) ready.Enqueue(item);
        }
        catch (TimeoutException) { }
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _translations.Writer.TryComplete();
        try { await CompleteAsync().ConfigureAwait(false); } catch { }
        _stop.Dispose();
    }

    internal sealed record PreparedItem(VideoSpeechTranslationText Translation,
        string TtsText, PreparedDubbingSpeech Speech);
}

internal sealed class ReadyAudioReserve
{
    private readonly object _sync = new();
    private int _count;
    private double _seconds;

    public void Add(TimeSpan duration)
    {
        lock (_sync)
        {
            _count++;
            _seconds += Math.Max(0, duration.TotalSeconds);
        }
    }

    public void Remove(TimeSpan duration)
    {
        lock (_sync)
        {
            _count = Math.Max(0, _count - 1);
            _seconds = Math.Max(0, _seconds - Math.Max(0, duration.TotalSeconds));
        }
    }

    public VideoDubbingReserveSnapshot Snapshot()
    {
        lock (_sync) return new VideoDubbingReserveSnapshot(_count, _seconds);
    }
}

internal sealed record VideoDubbingReserveSnapshot(int Phrases, double Seconds);

/// <summary>
/// Session-local JSONL log. It stores text and timing metadata only; captured
/// audio is never persisted. The Release build keeps this next to the executable.
/// </summary>
internal sealed class VideoDubbingDiagnosticLog : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly StreamWriter? _writer;

    public VideoDubbingDiagnosticLog(VideoTranslationMode mode)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory,
                "Diagnostics", "VideoDubbing");
            Directory.CreateDirectory(directory);
            FilePath = Path.Combine(directory,
                $"video-dubbing-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl");
            _writer = new StreamWriter(FilePath, append: false,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
            Mode = mode;
            _writer.WriteLine(JsonSerializer.Serialize(new
            {
                TimestampUtc = DateTimeOffset.UtcNow,
                Stage = "session-start",
                Mode = Mode.ToString()
            }));
            _writer.Flush();
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("video-translation",
                "diagnostic-log-open", ex);
        }
    }

    public string FilePath { get; } = string.Empty;
    public VideoTranslationMode Mode { get; }

    public async Task WriteAsync(string stage, VideoDubbingBuffer.PreparedItem item,
        VideoDubbingReserveSnapshot reserve)
    {
        if (_writer is null) return;
        var payload = JsonSerializer.Serialize(new
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Stage = stage,
            Mode = Mode.ToString(),
            SourceStartedAt = item.Translation.StartedAt,
            SourceEndedAt = item.Translation.EndedAt,
            AsrWindow = item.Translation.TranscriptWindow,
            AsrText = item.Translation.Transcript,
            SourceLanguage = item.Translation.SourceLanguage,
            ContextPhraseCount = item.Translation.ContextPhraseCount,
            Translation = item.Translation.RussianText,
            TtsText = item.TtsText,
            PreparedAudioSeconds = item.Speech.Duration.TotalSeconds,
            ReadyPhraseCount = reserve.Phrases,
            ReadyAudioSeconds = reserve.Seconds
        });
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(payload).ConfigureAwait(false);
            await _writer.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("video-translation",
                "diagnostic-log-write", ex);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_writer is null)
        {
            _gate.Dispose();
            return;
        }
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await _writer.DisposeAsync().ConfigureAwait(false); }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }
}
