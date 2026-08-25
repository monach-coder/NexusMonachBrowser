using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Threading.Channels;

namespace NexusMonach.Services;

internal interface IContinuousAudioCaptureSession : IAsyncDisposable
{
    bool IsProcessIsolated { get; }
    IAsyncEnumerable<SystemAudioCaptureService.AudioSegment> ReadSegmentsAsync(
        CancellationToken cancellationToken = default);
    void SuspendForDubbing();
    void Resume();
}

/// <summary>
/// Резервный захват текущего системного звука для роликов, чей HTML5/DRM-плеер
/// не предоставляет captureStream. Работает только во время явно запущенного
/// пользователем перевода видео и не сохраняет запись после распознавания.
/// </summary>
public static class SystemAudioCaptureService
{
    public sealed record AudioSegment(long Sequence, byte[] Wav, double Rms, double Peak,
        DateTimeOffset CapturedAt);

    /// <summary>
    /// Один непрерывный WASAPI-сеанс. Пока Whisper и OPUS обрабатывают текущую
    /// реплику, следующие сэмплы продолжают попадать в ограниченный буфер.
    /// Старые необработанные сегменты вытесняются, поэтому память не растёт.
    /// На endpoint-резерве видео удерживается во время Nexus Voice: так вход
    /// можно приостановить без потери продолжающейся речи источника.
    /// </summary>
    public sealed class ContinuousCaptureSession : IContinuousAudioCaptureSession
    {
        private readonly WasapiLoopbackCapture _capture = new();
        private readonly Channel<AudioSegment> _segments;
        private readonly CancellationTokenSource _stop = new();
        private readonly MemoryStream _raw = new();
        private readonly object _sync = new();
        private readonly int _segmentMilliseconds;
        private readonly int _overlapBytes;
        private readonly Task _segmentLoop;
        private long _sequence;
        private bool _disposed;
        private bool _suspended;
        private Exception? _recordingError;
        private bool _reportedEmptyWindow;
        private bool _reportedQuietWindow;
        private bool _reportedAudioWindow;

        internal ContinuousCaptureSession(int segmentMilliseconds, int overlapMilliseconds)
        {
            _segmentMilliseconds = Math.Clamp(segmentMilliseconds, 2_200, 12_000);
            var bytesPerSecond = _capture.WaveFormat.AverageBytesPerSecond;
            _overlapBytes = Align(bytesPerSecond * Math.Clamp(overlapMilliseconds, 0, 1_500) / 1_000,
                _capture.WaveFormat.BlockAlign);
            _segments = Channel.CreateBounded<AudioSegment>(new BoundedChannelOptions(
                VideoDubbingPolicy.MaxBufferedSegments)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = true
            });
            _capture.DataAvailable += CaptureOnDataAvailable;
            _capture.RecordingStopped += CaptureOnRecordingStopped;
            _capture.StartRecording();
            _segmentLoop = Task.Run(SegmentLoopAsync);
        }

        public bool IsProcessIsolated => false;

        public IAsyncEnumerable<AudioSegment> ReadSegmentsAsync(CancellationToken cancellationToken = default) =>
            _segments.Reader.ReadAllAsync(cancellationToken);

        /// <summary>
        /// На время закадрового голоса останавливает только поступление новых
        /// loopback-сэмплов. Вызывающий код одновременно удерживает видео, поэтому
        /// исходная реплика не продолжает идти и не теряется за голосом Nexus.
        /// </summary>
        public void SuspendForDubbing()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _suspended = true;
            }
        }

        public void Resume()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _suspended = false;
            }
        }

        private void CaptureOnDataAvailable(object? sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded <= 0) return;
            lock (_sync)
            {
                if (_disposed || _suspended) return;
                _raw.Write(e.Buffer, 0, e.BytesRecorded);
            }
        }

        private void CaptureOnRecordingStopped(object? sender, StoppedEventArgs e)
        {
            _recordingError = e.Exception;
            if (e.Exception is not null)
                _segments.Writer.TryComplete(new InvalidOperationException(
                    "Windows остановил захват системного звука: " + e.Exception.Message, e.Exception));
        }

        private async Task SegmentLoopAsync()
        {
            try
            {
                using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_segmentMilliseconds));
                while (await timer.WaitForNextTickAsync(_stop.Token))
                {
                    byte[] snapshot;
                    lock (_sync)
                    {
                        // Keep the partial phrase that arrived immediately before Nexus started
                        // speaking. It is joined with the next original samples after resume,
                        // instead of being silently discarded at a word boundary.
                        if (_suspended) continue;
                        snapshot = _raw.ToArray();
                        var keep = Math.Min(_overlapBytes, snapshot.Length);
                        _raw.SetLength(0);
                        if (keep > 0) _raw.Write(snapshot, snapshot.Length - keep, keep);
                    }
                    if (snapshot.Length < _capture.WaveFormat.AverageBytesPerSecond * 5 / 4)
                    {
                        if (!_reportedEmptyWindow)
                        {
                            _reportedEmptyWindow = true;
                            CrashReportService.AddBreadcrumb("video-translation",
                                $"system-loopback-short-{snapshot.Length}");
                        }
                        continue;
                    }
                    var converted = ConvertRawToWav(snapshot, _capture.WaveFormat);
                    // Очень тихие окна не отправляются в Whisper. Это уменьшает
                    // ложное распознавание и лишнюю нагрузку, но не отрезает тихую речь.
                    if (!VideoDubbingPolicy.IsAudible(converted.Rms, converted.Peak))
                    {
                        if (!_reportedQuietWindow)
                        {
                            _reportedQuietWindow = true;
                            CrashReportService.AddBreadcrumb("video-translation",
                                $"system-loopback-quiet-rms-{converted.Rms:F6}-peak-{converted.Peak:F6}");
                        }
                        continue;
                    }
                    if (!_reportedAudioWindow)
                    {
                        _reportedAudioWindow = true;
                        CrashReportService.AddBreadcrumb("video-translation",
                            $"system-loopback-audio-rms-{converted.Rms:F4}-peak-{converted.Peak:F4}");
                    }
                    _segments.Writer.TryWrite(new AudioSegment(
                        Interlocked.Increment(ref _sequence), converted.Wav,
                        converted.Rms, converted.Peak,
                        DateTimeOffset.UtcNow - TimeSpan.FromMilliseconds(_segmentMilliseconds)));
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { _segments.Writer.TryComplete(ex); }
            finally { _segments.Writer.TryComplete(_recordingError); }
        }

        public async ValueTask DisposeAsync()
        {
            lock (_sync)
            {
                if (_disposed) return;
                _disposed = true;
            }
            _stop.Cancel();
            try { _capture.StopRecording(); } catch { }
            try { await _segmentLoop; } catch { }
            _capture.DataAvailable -= CaptureOnDataAvailable;
            _capture.RecordingStopped -= CaptureOnRecordingStopped;
            _capture.Dispose();
            _raw.Dispose();
            _stop.Dispose();
        }

        private static int Align(int value, int blockAlign) =>
            blockAlign <= 1 ? value : value - value % blockAlign;
    }

    public static ContinuousCaptureSession StartContinuousCapture(
        int segmentMilliseconds = 2_800, int overlapMilliseconds = 300) =>
        new(segmentMilliseconds, overlapMilliseconds);

    internal static async Task<IContinuousAudioCaptureSession> StartPreferredContinuousCaptureAsync(
        int targetProcessId, int segmentMilliseconds = 2_800, int overlapMilliseconds = 300,
        CancellationToken cancellationToken = default)
    {
        if (VideoDubbingPolicy.SupportsProcessLoopback(Environment.OSVersion.Version, targetProcessId))
            try
            {
                return await ProcessAudioCaptureService.StartAsync(targetProcessId,
                    segmentMilliseconds, overlapMilliseconds, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                CrashReportService.RecordNonFatal("video-translation", "process-loopback-fallback", ex);
            }

        return StartContinuousCapture(segmentMilliseconds, overlapMilliseconds);
    }

    public static async Task<byte[]> CaptureWavAsync(int seconds, CancellationToken cancellationToken = default)
    {
        seconds = Math.Clamp(seconds, 3, 15);
        using var rawAudio = new MemoryStream();
        using var capture = new WasapiLoopbackCapture();
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? recordingError = null;

        void DataAvailable(object? _, WaveInEventArgs e)
        {
            if (e.BytesRecorded > 0)
                rawAudio.Write(e.Buffer, 0, e.BytesRecorded);
        }
        void RecordingStopped(object? _, StoppedEventArgs e)
        {
            recordingError = e.Exception;
            stopped.TrySetResult(true);
        }
        capture.DataAvailable += DataAvailable;
        capture.RecordingStopped += RecordingStopped;

        capture.StartRecording();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
        finally
        {
            try { capture.StopRecording(); } catch { }
        }
        try { await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None); }
        catch (TimeoutException) when (cancellationToken.IsCancellationRequested) { }
        finally
        {
            capture.DataAvailable -= DataAvailable;
            capture.RecordingStopped -= RecordingStopped;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (recordingError is not null)
            throw new InvalidOperationException("Windows не передал системный звук: " + recordingError.Message,
                recordingError);

        if (rawAudio.Length == 0)
            throw new InvalidOperationException("Системный выход не передал аудиосэмплы. Проверь, что видео воспроизводится и звук не отключён.");

        return ConvertRawToWav(rawAudio.ToArray(), capture.WaveFormat).Wav;
    }

    internal static (byte[] Wav, double Rms, double Peak) ConvertRawToWav(byte[] rawAudio,
        WaveFormat waveFormat)
    {
        // WASAPI обычно отдаёт 48 кГц float/stereo, тогда как whisper.cpp принимает
        // подготовленный 16 кГц mono PCM WAV. Преобразование остаётся целиком в памяти.
        using var rawStream = new RawSourceWaveStream(new MemoryStream(rawAudio), waveFormat);
        ISampleProvider samples = rawStream.ToSampleProvider();
        samples = samples.WaveFormat.Channels switch
        {
            1 => samples,
            2 => new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f },
            _ => new MultiChannelToMonoSampleProvider(samples)
        };
        if (samples.WaveFormat.SampleRate != 16_000)
            samples = new WdlResamplingSampleProvider(samples, 16_000);

        var monoSamples = new List<float>(Math.Max(16_000,
            rawAudio.Length / Math.Max(1, waveFormat.BlockAlign)));
        double squared = 0;
        double peak = 0;
        long sampleCount = 0;
        var buffer = new float[16_000];
        int read;
        while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (var index = 0; index < read; index++)
            {
                var sample = Math.Clamp(buffer[index], -1f, 1f);
                monoSamples.Add(sample);
                squared += sample * sample;
                peak = Math.Max(peak, Math.Abs(sample));
                sampleCount++;
            }
        }
        var rms = sampleCount == 0 ? 0 : Math.Sqrt(squared / sampleCount);
        var gain = VideoDubbingPolicy.SelectRecognitionGain(rms, peak);
        using var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(wav, new WaveFormat(16_000, 16, 1)))
            foreach (var sample in monoSamples)
                writer.WriteSample((float)Math.Clamp(sample * gain, -0.98, 0.98));
        return (wav.ToArray(), rms, peak);
    }

    /// <summary>
    /// Windows/HDMI often exposes the loopback endpoint as 5.1 or 7.1 even when
    /// the current video is stereo. Whisper needs mono, so average every channel
    /// instead of rejecting perfectly valid system audio.
    /// </summary>
    private sealed class MultiChannelToMonoSampleProvider : ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channels;
        private float[] _sourceBuffer = [];

        public MultiChannelToMonoSampleProvider(ISampleProvider source)
        {
            _source = source;
            _channels = source.WaveFormat.Channels;
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(source.WaveFormat.SampleRate, 1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(float[] buffer, int offset, int count)
        {
            var required = count * _channels;
            if (_sourceBuffer.Length < required) _sourceBuffer = new float[required];
            var sourceRead = _source.Read(_sourceBuffer, 0, required);
            var frames = sourceRead / _channels;
            for (var frame = 0; frame < frames; frame++)
            {
                float sum = 0;
                var sourceOffset = frame * _channels;
                for (var channel = 0; channel < _channels; channel++)
                    sum += _sourceBuffer[sourceOffset + channel];
                buffer[offset + frame] = sum / _channels;
            }
            return frames;
        }
    }
}
