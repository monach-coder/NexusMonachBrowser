using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NexusMonach.Services;

/// <summary>
/// Резервный захват текущего системного звука для роликов, чей HTML5/DRM-плеер
/// не предоставляет captureStream. Работает только во время явно запущенного
/// пользователем перевода видео и не сохраняет запись после распознавания.
/// </summary>
public static class SystemAudioCaptureService
{
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

        // WASAPI обычно отдаёт 48 кГц float/stereo, тогда как whisper.cpp принимает
        // подготовленный 16 кГц mono PCM WAV. Преобразование остаётся целиком в памяти.
        using var rawStream = new RawSourceWaveStream(new MemoryStream(rawAudio.ToArray()), capture.WaveFormat);
        ISampleProvider samples = rawStream.ToSampleProvider();
        samples = samples.WaveFormat.Channels switch
        {
            1 => samples,
            2 => new StereoToMonoSampleProvider(samples) { LeftVolume = 0.5f, RightVolume = 0.5f },
            _ => new MultiChannelToMonoSampleProvider(samples)
        };
        if (samples.WaveFormat.SampleRate != 16_000)
            samples = new WdlResamplingSampleProvider(samples, 16_000);

        using var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(wav, new WaveFormat(16_000, 16, 1)))
        {
            var buffer = new float[16_000];
            int read;
            while ((read = samples.Read(buffer, 0, buffer.Length)) > 0)
                for (var index = 0; index < read; index++)
                    writer.WriteSample(buffer[index]);
        }
        return wav.ToArray();
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
