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
        using var capture = new WasapiLoopbackCapture();
        using var rawAudio = new MemoryStream();
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Exception? recordingError = null;

        capture.DataAvailable += (_, e) =>
        {
            if (e.BytesRecorded > 0)
                rawAudio.Write(e.Buffer, 0, e.BytesRecorded);
        };
        capture.RecordingStopped += (_, e) =>
        {
            recordingError = e.Exception;
            stopped.TrySetResult(true);
        };

        capture.StartRecording();
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(seconds), cancellationToken);
        }
        finally
        {
            try { capture.StopRecording(); } catch { }
        }
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
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
            _ => throw new InvalidOperationException($"Неподдерживаемое число системных аудиоканалов: {samples.WaveFormat.Channels}.")
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
}
