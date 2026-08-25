using System.Collections.Concurrent;
using NAudio.Wave;

namespace NexusMonach.Services;

/// <summary>
/// Одна реплика готового дубляжа: таймкод источника и цепочка WAV-файлов
/// (длинный перевод делится на части, они проигрываются подряд).
/// </summary>
internal sealed record PrecomputedDubbingPhrase(
    double StartSeconds,
    double EndSeconds,
    IReadOnlyList<string> WavPaths,
    string RussianText);

/// <summary>
/// Плеер готового дубляжа: играет цепочки WAV строго последовательно на
/// выделенном потоке, поддерживает паузу вместе с видео и мгновенный стоп.
/// Временные файлы удаляются при завершении сессии.
/// </summary>
internal sealed class PrecomputedDubbingPlayer : IDisposable
{
    private readonly object _sync = new();
    private readonly Thread _thread;
    private readonly BlockingCollection<IReadOnlyList<string>> _queue = new(32);
    private WaveOutEvent? _output;
    private WaveFileReader? _reader;
    private bool _paused;
    private bool _disposed;
    private long _playedChains;

    public PrecomputedDubbingPlayer()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Nexus precomputed dubbing" };
        _thread.Start();
    }

    /// <summary>Сыгранные цепочки реплик — телеметрия для статуса сессии.</summary>
    public long PlayedChains => Interlocked.Read(ref _playedChains);

    /// <summary>Глубина очереди воспроизведения.</summary>
    public int QueueDepth => _queue.Count;

    /// <summary>Жив ли поток воспроизведения.</summary>
    public bool ThreadAlive => _thread.IsAlive;

    public bool IsPlaying
    {
        get
        {
            lock (_sync)
                return !_queue.IsCompleted && (_queue.Count > 0 || _output is { PlaybackState: PlaybackState.Playing });
        }
    }

    public void Enqueue(IReadOnlyList<string> wavPaths)
    {
        if (wavPaths.Count == 0) return;
        lock (_sync)
        {
            if (_disposed) return;
        }
        while (!_queue.TryAdd(wavPaths))
            _queue.TryTake(out _);
    }

    public void Pause()
    {
        lock (_sync)
        {
            _paused = true;
            try { _output?.Pause(); } catch (Exception swallowed)
            {
                Services.SwallowLog.Log("precomputed-dubbing", "Pause", swallowed);
            }
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            _paused = false;
            try { _output?.Play(); } catch (Exception swallowed)
            {
                Services.SwallowLog.Log("precomputed-dubbing", "Resume", swallowed);
            }
            Monitor.Pulse(_sync);
        }
    }

    public void Dispose()
    {
        List<string> pendingFiles;
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            Monitor.Pulse(_sync);
        }
        _queue.CompleteAdding();
        try { _thread.Join(3000); } catch (Exception swallowed)
        {
            Services.SwallowLog.Log("precomputed-dubbing", "Dispose", swallowed);
        }
        lock (_sync)
        {
            StopCurrentLocked();
            pendingFiles = DrainQueueLocked();
        }
        _queue.Dispose();
        foreach (var path in pendingFiles)
            try { File.Delete(path); } catch (Exception swallowed)
            {
                Services.SwallowLog.Log("precomputed-dubbing", "Dispose", swallowed);
            }
    }

    private void Run()
    {
        try
        {
            foreach (var chain in _queue.GetConsumingEnumerable())
            {
                foreach (var path in chain)
                {
                    try
                    {
                        WaveOutEvent output;
                        WaveFileReader reader;
                        lock (_sync)
                        {
                            if (_disposed) { try { File.Delete(path); } catch (Exception swallowed)
                            {
                                Services.SwallowLog.Log("precomputed-dubbing", "Run", swallowed);
                            } return; }
                            while (_paused && !_disposed)
                                Monitor.Wait(_sync);
                            if (_disposed) { try { File.Delete(path); } catch (Exception swallowed)
                            {
                                Services.SwallowLog.Log("precomputed-dubbing", "Run", swallowed);
                            } return; }
                            StopCurrentLocked();
                            reader = new WaveFileReader(path);
                            output = new WaveOutEvent();
                            _reader = reader;
                            _output = output;
                        }
                        using var completed = new ManualResetEventSlim(false);
                        output.PlaybackStopped += (_, _) => completed.Set();
                        try
                        {
                            output.Init(reader);
                            // Перевод — главный голос: полная громкость устройства,
                            // оригинал к этому моменту приглушён до фона.
                            output.Volume = 1.0f;
                            output.Play();
                            // Watchdog: если аудиоустройство не отдаёт события
                            // (виртуальные/переключенные выходы), реплика не
                            // имеет права блокировать плеер навсегда.
                            if (!completed.Wait(TimeSpan.FromSeconds(35)))
                            {
                                CrashReportService.RecordNonFatal("video-translation",
                                    "dubbing-play-watchdog",
                                    new InvalidOperationException(
                                        "Аудиовыход не завершил реплику за 35 с — принудительный переход."));
                            }
                        }
                        catch
                        {
                            // Повреждённый или занятый файл не должен ронять сессию.
                        }
                        lock (_sync)
                        {
                            StopCurrentLocked();
                        }
                    }
                    catch
                    {
                        // Ни одно исключение не имеет права убить поток плеера:
                        // мёртвый поток оставит очередь замороженной, IsPlaying
                        // навсегда true — и планировщик перестанет ставить реплики.
                        lock (_sync)
                        {
                            StopCurrentLocked();
                        }
                    }
                    try { File.Delete(path); } catch (Exception swallowed)
                    {
                        Services.SwallowLog.Log("precomputed-dubbing", "Run", swallowed);
                    }
                }
                Interlocked.Increment(ref _playedChains);
            }
        }
        catch (OperationCanceledException swallowed)
        {
            Services.SwallowLog.Log("precomputed-dubbing", "Run", swallowed);
        }
        catch
        {
            // Плеер фоновый: любые сбои просто завершают цепочку.
        }
        finally
        {
            lock (_sync) StopCurrentLocked();
        }
    }

    private void StopCurrentLocked()
    {
        try { _output?.Stop(); } catch (Exception swallowed)
        {
            Services.SwallowLog.Log("precomputed-dubbing", "StopCurrentLocked", swallowed);
        }
        try { _output?.Dispose(); } catch (Exception swallowed)
        {
            Services.SwallowLog.Log("precomputed-dubbing", "StopCurrentLocked", swallowed);
        }
        try { _reader?.Dispose(); } catch (Exception swallowed)
        {
            Services.SwallowLog.Log("precomputed-dubbing", "StopCurrentLocked", swallowed);
        }
        _output = null;
        _reader = null;
    }

    private List<string> DrainQueueLocked()
    {
        var files = new List<string>();
        while (_queue.TryTake(out var chain))
            files.AddRange(chain);
        return files;
    }
}
