using System.Media;
using System.Runtime.InteropServices;

namespace NexusMonach.Services;

public static class StartupSoundService
{
    public static Task PlayAsync()
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                PlayChime();
                SpeakBrandName();
                WriteDiagnostic("OK: chime and spoken brand completed.");
                completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                // Звук не должен мешать запуску, но ошибка больше не теряется молча.
                WriteDiagnostic(ex.ToString());
                completion.TrySetResult(false);
            }
        })
        {
            IsBackground = true,
            Name = "Nexus startup voice"
        };
        // SAPI надёжнее работает в отдельном STA-потоке. Task.Run создавал MTA-поток,
        // из-за чего на части Windows голос и даже весь стартовый сценарий молча пропускались.
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void PlayChime()
    {
        using var stream = new MemoryStream(BuildChime(), writable: false);
        using var player = new SoundPlayer(stream);
        player.Load();
        player.PlaySync();
    }

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
            writer.Write((short)(Math.Clamp(value * masterFade * 0.24, -1, 1) * short.MaxValue));
        }
        writer.Flush();
        return stream.ToArray();
    }

    private static void SpeakBrandName()
    {
        object? voiceObject = null;
        try
        {
            var sapiType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (sapiType is null) throw new InvalidOperationException("Windows SAPI.SpVoice не зарегистрирован.");
            voiceObject = Activator.CreateInstance(sapiType)
                          ?? throw new InvalidOperationException("Windows не создал SAPI.SpVoice.");
            dynamic voice = voiceObject;
            dynamic maleVoices = voice.GetVoices("Gender=Male", "");
            if (maleVoices.Count > 0) voice.Voice = maleVoices.Item(0);
            voice.Rate = -1;
            voice.Volume = 92;
            // Кириллическая запись одинаково читается русскими голосами Windows.
            voice.Speak("Нексус Монах", 0);
        }
        finally
        {
            if (voiceObject is not null && Marshal.IsComObject(voiceObject))
                try { Marshal.FinalReleaseComObject(voiceObject); } catch { }
        }
    }

    private static void WriteDiagnostic(string text)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.AppRoot);
            File.AppendAllText(Path.Combine(AppPaths.AppRoot, "startup-audio.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}{Environment.NewLine}");
        }
        catch { }
    }
}
