using NexusMonach.Services;
using Xunit;

namespace NexusMonach.Security.Tests;

public class AudioRateRestoreTests
{
    private static byte[] BuildWav(short[] samples, int sampleRate = 16000)
    {
        var bytes = new byte[44 + samples.Length * 2];
        "RIFF"u8.ToArray().CopyTo(bytes, 0);
        WriteUInt32(bytes, 4, (uint)(36 + samples.Length * 2));
        "WAVE"u8.ToArray().CopyTo(bytes, 8);
        "fmt "u8.ToArray().CopyTo(bytes, 12);
        WriteUInt32(bytes, 16, 16);
        bytes[20] = 1;
        bytes[22] = 1;
        WriteUInt32(bytes, 24, (uint)sampleRate);
        WriteUInt32(bytes, 28, (uint)(sampleRate * 2));
        bytes[32] = 2;
        bytes[34] = 16;
        "data"u8.ToArray().CopyTo(bytes, 36);
        WriteUInt32(bytes, 40, (uint)(samples.Length * 2));
        for (var i = 0; i < samples.Length; i++)
        {
            bytes[44 + i * 2] = (byte)samples[i];
            bytes[45 + i * 2] = (byte)(samples[i] >> 8);
        }
        return bytes;
    }

    private static void WriteUInt32(byte[] bytes, int at, uint value)
    {
        bytes[at] = (byte)value;
        bytes[at + 1] = (byte)(value >> 8);
        bytes[at + 2] = (byte)(value >> 16);
        bytes[at + 3] = (byte)(value >> 24);
    }

    private static short ReadInt16(byte[] bytes, int at) =>
        (short)(bytes[at] | bytes[at + 1] << 8);

    [Fact]
    public void UnitRate_ReturnsOriginalBytes()
    {
        var wav = BuildWav(Enumerable.Range(0, 1600).Select(i => (short)(i % 100)).ToArray());
        Assert.Same(wav, AudioRateRestore.RestoreTempo(wav, 1.0));
    }

    [Fact]
    public void OutOfRangeRate_ReturnsOriginalBytes()
    {
        var wav = BuildWav(new short[1600]);
        Assert.Same(wav, AudioRateRestore.RestoreTempo(wav, 0.1));
        Assert.Same(wav, AudioRateRestore.RestoreTempo(wav, 32));
    }

    [Fact]
    public void QuadRate_RestoresFourfoldDuration()
    {
        // 1 секунда речи, ужатая ×4 → после восстановления ≈4 секунды.
        var wav = BuildWav(new short[16000]);
        var restored = AudioRateRestore.RestoreTempo(wav, 4);
        var duration = AudioRateRestore.PcmDurationSeconds(restored);
        Assert.InRange(duration, 3.98, 4.02);
    }

    [Fact]
    public void ConstantTone_StaysConstantAfterStretch()
    {
        var wav = BuildWav(Enumerable.Repeat((short)1000, 8000).ToArray());
        var restored = AudioRateRestore.RestoreTempo(wav, 2.5);
        var frames = (restored.Length - 44) / 2;
        Assert.InRange(frames, 19999, 20001);
        for (var i = 0; i < frames; i++)
            Assert.InRange(ReadInt16(restored, 44 + i * 2), 999, 1001);
    }

    [Fact]
    public void Silence_StaysSilent()
    {
        var wav = BuildWav(new short[32000]);
        var restored = AudioRateRestore.RestoreTempo(wav, 3);
        for (var i = 44; i < restored.Length; i++)
            Assert.Equal(0, restored[i]);
    }

    [Fact]
    public void DurationSeconds_ReadsHeader()
    {
        var wav = BuildWav(new short[32000]);
        Assert.Equal(2.0, AudioRateRestore.PcmDurationSeconds(wav), 3);
    }

    [Fact]
    public void ExtraListChunk_DurationAndSliceUseRealDataOffset()
    {
        // ffmpeg пишет WAV с чанком LIST: данные начинаются позже 44-го
        // байта, и нарезка «в лоб по 44» даёт мусор — ловится whisper'ом
        // как «failed to read audio file».
        var samples = new short[16000]; // 1 секунда
        var core = BuildWav(samples);
        var listPad = 26;
        // Плоский макет: RIFF + WAVE + LIST(26) + fmt + data из канонического
        // WAV — ровно как пишет ffmpeg.
        var tail = core.Length - 12; // fmt и data без RIFF-заголовка
        var rebuilt = new byte[12 + 8 + listPad + tail];
        rebuilt[0] = (byte)'R'; rebuilt[1] = (byte)'I'; rebuilt[2] = (byte)'F'; rebuilt[3] = (byte)'F';
        rebuilt[8] = (byte)'W'; rebuilt[9] = (byte)'A'; rebuilt[10] = (byte)'V'; rebuilt[11] = (byte)'E';
        rebuilt[12] = (byte)'L'; rebuilt[13] = (byte)'I'; rebuilt[14] = (byte)'S'; rebuilt[15] = (byte)'T';
        rebuilt[16] = (byte)listPad; rebuilt[17] = (byte)(listPad >> 8);
        core.AsSpan(12).CopyTo(rebuilt.AsSpan(12 + 8 + listPad));

        Assert.Equal(1.0, AudioRateRestore.PcmDurationSeconds(rebuilt), 3);
        var slice = AudioRateRestore.SliceByTime(rebuilt, 0, 0.5);
        Assert.Equal(0.5, AudioRateRestore.PcmDurationSeconds(slice), 3);
        foreach (var sample in slice.Skip(44))
            Assert.Equal(0, sample);
    }
}
