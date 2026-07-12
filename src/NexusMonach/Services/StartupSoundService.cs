using System.Media;

namespace NexusMonach.Services;

public static class StartupSoundService
{
    public static Task PlayAsync() => Task.Run(() =>
    {
        try
        {
            using var stream = new MemoryStream(BuildChime());
            using var player = new SoundPlayer(stream);
            player.PlaySync();
            SpeakBrandName();
        }
        catch { /* Отсутствие аудиоустройства не должно мешать запуску браузера. */ }
    });

    private static byte[] BuildChime()
    {
        const int sampleRate = 44100;
        const double duration = 0.82;
        var sampleCount = (int)(sampleRate * duration);
        using var stream = new MemoryStream(44 + sampleCount * 2);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8.ToArray());
        writer.Write(36 + sampleCount * 2);
        writer.Write("WAVEfmt "u8.ToArray());
        writer.Write(16); writer.Write((short)1); writer.Write((short)1);
        writer.Write(sampleRate); writer.Write(sampleRate * 2);
        writer.Write((short)2); writer.Write((short)16);
        writer.Write("data"u8.ToArray()); writer.Write(sampleCount * 2);

        double[] frequencies = [587.33, 880.0, 1318.51];
        double[] starts = [0.0, 0.16, 0.34];
        for (var i = 0; i < sampleCount; i++)
        {
            var time = i / (double)sampleRate;
            var value = 0.0;
            for (var tone = 0; tone < frequencies.Length; tone++)
            {
                var local = time - starts[tone];
                if (local < 0) continue;
                var envelope = Math.Exp(-4.8 * local) * Math.Min(1, local / 0.018);
                value += Math.Sin(2 * Math.PI * frequencies[tone] * local) * envelope;
                value += Math.Sin(2 * Math.PI * frequencies[tone] * 2 * local) * envelope * 0.12;
            }
            var masterFade = Math.Min(1, (duration - time) / 0.12);
            writer.Write((short)(Math.Clamp(value * masterFade * 0.105, -1, 1) * short.MaxValue));
        }
        return stream.ToArray();
    }

    private static void SpeakBrandName()
    {
        try
        {
            var sapiType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (sapiType is null) return;
            dynamic voice = Activator.CreateInstance(sapiType)!;
            dynamic maleVoices = voice.GetVoices("Gender=Male", "");
            if (maleVoices.Count > 0) voice.Voice = maleVoices.Item(0);
            voice.Rate = -1;
            voice.Volume = 78;
            voice.Speak("Nexus Monach", 0);
        }
        catch { /* SAPI или мужской голос могут отсутствовать в облегчённой Windows. */ }
    }
}
