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
}
