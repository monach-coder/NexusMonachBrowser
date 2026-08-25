namespace NexusMonach.Services;

/// <summary>
/// Восстановление естественного темпа речи после ускоренного прогона и
/// работа с WAV-макетами. ffmpeg пишет WAV с дополнительными чанками
/// (например, LIST), поэтому данные не обязаны начинаться с байта 44 —
/// все операции сначала честно разбирают макет файла.
/// </summary>
public static class AudioRateRestore
{
    /// <summary>Макет PCM-данных внутри WAV.</summary>
    public readonly record struct WavLayout(
        int DataOffset, int DataLength, int SampleRate, int Channels, int Bits);

    /// <summary>Разбирает чанки WAV и находит область PCM-данных.</summary>
    public static bool TryGetLayout(byte[] wav, out WavLayout layout)
    {
        layout = default;
        if (wav.Length < 44 ||
            wav[0] != (byte)'R' || wav[1] != (byte)'I' || wav[2] != (byte)'F' || wav[3] != (byte)'F' ||
            wav[8] != (byte)'W' || wav[9] != (byte)'A' || wav[10] != (byte)'V' || wav[11] != (byte)'E')
            return false;
        var sampleRate = 0;
        var channels = 0;
        var bits = 0;
        var dataOffset = -1;
        var dataLength = 0;
        var offset = 12;
        while (offset + 8 <= wav.Length)
        {
            var size = (int)ReadUInt32(wav, offset + 4);
            if (IsChunk(wav, offset, "fmt "))
            {
                if (offset + 8 + 16 <= wav.Length)
                {
                    // Тело fmt начинается после заголовка чанка: формат(+8),
                    // каналы(+10), частота(+12), биты на сэмпл(+22).
                    channels = ReadUInt16(wav, offset + 10);
                    sampleRate = (int)ReadUInt32(wav, offset + 12);
                    bits = ReadUInt16(wav, offset + 22);
                }
            }
            else if (IsChunk(wav, offset, "data"))
            {
                dataOffset = offset + 8;
                dataLength = Math.Min(size, wav.Length - dataOffset);
                break;
            }
            if (size < 0 || offset + 8 + size > wav.Length) break;
            offset += 8 + size + (size & 1);
        }
        if (dataOffset < 0 || sampleRate <= 0 || channels <= 0 || bits != 16 || dataLength <= 0)
            return false;
        layout = new WavLayout(dataOffset, dataLength, sampleRate, channels, bits);
        return true;
    }

    /// <summary>Длительность PCM-данных WAV в секундах (по реальному макету).</summary>
    public static double PcmDurationSeconds(byte[] wav) =>
        TryGetLayout(wav, out var layout)
            ? layout.DataLength / 2.0 / layout.Channels / layout.SampleRate
            : 0;

    /// <summary>
    /// Вырезает участок [fromSeconds, toSeconds) и собирает канонический
    /// WAV с теми же параметрами звука. Ничего не знает про «44 байта».
    /// </summary>
    public static byte[] SliceByTime(byte[] wav, double fromSeconds, double toSeconds)
    {
        if (!TryGetLayout(wav, out var layout) || layout.Channels != 1) return [];
        var total = PcmDurationSeconds(wav);
        fromSeconds = Math.Max(0, fromSeconds);
        toSeconds = Math.Min(total, toSeconds);
        if (toSeconds <= fromSeconds) return [];
        var from = (int)Math.Floor(fromSeconds * layout.SampleRate);
        var to = (int)Math.Ceiling(toSeconds * layout.SampleRate);
        var available = layout.DataLength / 2;
        from = Math.Min(from, available);
        to = Math.Min(to, available);
        if (to <= from) return [];
        var samples = to - from;
        var output = new byte[44 + samples * 2];
        WriteCanonicalHeader(output, samples * 2, layout.SampleRate, 1);
        wav.AsSpan(layout.DataOffset + from * 2, samples * 2).CopyTo(output.AsSpan(44));
        return output;
    }

    /// <summary>Возвращает копию WAV с темпом, восстановленным до ×1.</summary>
    public static byte[] RestoreTempo(byte[] wav, double rate)
    {
        if (rate < 0.5 || rate > 16 || Math.Abs(rate - 1) < 0.02 ||
            !TryGetLayout(wav, out var layout) || layout.Channels != 1)
            return wav;
        var frames = layout.DataLength / 2;
        if (frames < 2) return wav;
        var outFrames = (int)Math.Round(frames * rate);
        var output = new byte[44 + outFrames * 2];
        WriteCanonicalHeader(output, outFrames * 2, layout.SampleRate, 1);
        for (var i = 0; i < outFrames; i++)
        {
            var position = i / rate;
            var index = (int)position;
            int sample;
            if (index + 1 >= frames)
            {
                sample = ReadInt16(wav, layout.DataOffset + index * 2);
            }
            else
            {
                var left = ReadInt16(wav, layout.DataOffset + index * 2);
                var right = ReadInt16(wav, layout.DataOffset + (index + 1) * 2);
                sample = (int)Math.Round(left + (right - left) * (position - index));
            }
            output[44 + i * 2] = (byte)sample;
            output[45 + i * 2] = (byte)(sample >> 8);
        }
        return output;
    }

    /// <summary>Канонический 44-байтный заголовок PCM16 mono/stereo.</summary>
    public static void WriteCanonicalHeader(byte[] wav, int dataSize, int sampleRate, int channels)
    {
        wav[0] = (byte)'R'; wav[1] = (byte)'I'; wav[2] = (byte)'F'; wav[3] = (byte)'F';
        WriteUInt32(wav, 4, (uint)(36 + dataSize));
        wav[8] = (byte)'W'; wav[9] = (byte)'A'; wav[10] = (byte)'V'; wav[11] = (byte)'E';
        wav[12] = (byte)'f'; wav[13] = (byte)'m'; wav[14] = (byte)'t'; wav[15] = (byte)' ';
        WriteUInt32(wav, 16, 16);
        WriteUInt16(wav, 20, 1);
        WriteUInt16(wav, 22, (ushort)channels);
        WriteUInt32(wav, 24, (uint)sampleRate);
        WriteUInt32(wav, 28, (uint)(sampleRate * channels * 2));
        WriteUInt16(wav, 32, (ushort)(channels * 2));
        WriteUInt16(wav, 34, 16);
        wav[36] = (byte)'d'; wav[37] = (byte)'a'; wav[38] = (byte)'t'; wav[39] = (byte)'a';
        WriteUInt32(wav, 40, (uint)dataSize);
    }

    private static bool IsChunk(byte[] wav, int offset, string id) =>
        wav[offset] == (byte)id[0] && wav[offset + 1] == (byte)id[1] &&
        wav[offset + 2] == (byte)id[2] && wav[offset + 3] == (byte)id[3];

    private static ushort ReadUInt16(byte[] bytes, int at) =>
        (ushort)(bytes[at] | bytes[at + 1] << 8);

    private static uint ReadUInt32(byte[] bytes, int at) =>
        (uint)(bytes[at] | bytes[at + 1] << 8 |
               bytes[at + 2] << 16 | bytes[at + 3] << 24);

    private static short ReadInt16(byte[] bytes, int at) =>
        (short)(bytes[at] | bytes[at + 1] << 8);

    private static void WriteUInt16(byte[] bytes, int at, ushort value)
    {
        bytes[at] = (byte)value;
        bytes[at + 1] = (byte)(value >> 8);
    }

    private static void WriteUInt32(byte[] bytes, int at, uint value)
    {
        bytes[at] = (byte)value;
        bytes[at + 1] = (byte)(value >> 8);
        bytes[at + 2] = (byte)(value >> 16);
        bytes[at + 3] = (byte)(value >> 24);
    }
}
