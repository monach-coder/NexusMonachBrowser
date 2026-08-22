namespace NexusMonach.Services;

/// <summary>
/// Восстановление естественного темпа речи после ускоренного прогона.
/// При playbackRate R браузер отдаёт в WebAudio запись, сжатую по времени
/// в R раз (тон при этом сохраняется). Линейное растяжение в R раз возвращает
/// исходный темп — без него whisper не распознаёт «скороговорку» на ×4.
/// </summary>
public static class AudioRateRestore
{
    /// <summary>Возвращает копию WAV с темпом, восстановленным до ×1.</summary>
    public static byte[] RestoreTempo(byte[] wav, double rate)
    {
        if (wav.Length < 44 || rate < 0.5 || rate > 16 ||
            Math.Abs(rate - 1) < 0.02)
            return wav;
        var channels = ReadUInt16(wav, 22);
        var sampleRate = ReadUInt32(wav, 24);
        var bits = ReadUInt16(wav, 34);
        if (channels != 1 || bits != 16 || sampleRate == 0)
            return wav;
        var frames = (wav.Length - 44) / 2;
        if (frames < 2)
            return wav;
        var outFrames = (int)Math.Round(frames * rate);
        var output = new byte[44 + outFrames * 2];
        wav.AsSpan(0, 44).CopyTo(output);
        WriteUInt32(output, 4, (uint)(36 + outFrames * 2));
        WriteUInt32(output, 40, (uint)(outFrames * 2));
        for (var i = 0; i < outFrames; i++)
        {
            var position = i / rate;
            var index = (int)position;
            int sample;
            if (index + 1 >= frames)
            {
                sample = ReadInt16(wav, 44 + index * 2);
            }
            else
            {
                var left = ReadInt16(wav, 44 + index * 2);
                var right = ReadInt16(wav, 44 + (index + 1) * 2);
                sample = (int)Math.Round(left + (right - left) * (position - index));
            }
            output[44 + i * 2] = (byte)sample;
            output[45 + i * 2] = (byte)(sample >> 8);
        }
        return output;
    }

    /// <summary>Длительность PCM-данных WAV в секундах (по заголовку).</summary>
    public static double PcmDurationSeconds(byte[] wav)
    {
        if (wav.Length < 44) return 0;
        var sampleRate = ReadUInt32(wav, 24);
        var channels = ReadUInt16(wav, 22);
        if (sampleRate == 0 || channels == 0) return 0;
        return (wav.Length - 44) / 2.0 / channels / sampleRate;
    }

    private static ushort ReadUInt16(byte[] bytes, int at) =>
        (ushort)(bytes[at] | bytes[at + 1] << 8);

    private static uint ReadUInt32(byte[] bytes, int at) =>
        (uint)(bytes[at] | bytes[at + 1] << 8 |
               bytes[at + 2] << 16 | bytes[at + 3] << 24);

    private static short ReadInt16(byte[] bytes, int at) =>
        (short)(bytes[at] | bytes[at + 1] << 8);

    private static void WriteUInt32(byte[] bytes, int at, uint value)
    {
        bytes[at] = (byte)value;
        bytes[at + 1] = (byte)(value >> 8);
        bytes[at + 2] = (byte)(value >> 16);
        bytes[at + 3] = (byte)(value >> 24);
    }
}
