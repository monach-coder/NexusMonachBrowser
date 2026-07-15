using System.Media;
using System.Runtime.InteropServices;
using System.Speech.Synthesis;

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
                // SoundPlayer освобождает аудиоустройство асинхронно на части систем.
                // Короткая пауза не даёт началу фразы потеряться сразу после сигнала.
                Thread.Sleep(180);
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
        try
        {
            SpeakWithWindowsSynthesizer();
        }
        catch (Exception primaryError)
        {
            WriteDiagnostic($"System.Speech failed; trying SAPI fallback: {primaryError}");
            SpeakWithSapiFallback();
        }
    }

    private static void SpeakWithWindowsSynthesizer()
    {
        using var synthesizer = new SpeechSynthesizer();
        var voices = synthesizer.GetInstalledVoices()
            .Where(item => item.Enabled)
            .Select(item => item.VoiceInfo)
            .ToArray();

        if (voices.Length == 0)
            throw new InvalidOperationException("В Windows не найдено ни одного установленного голоса.");

        var selected = voices.FirstOrDefault(voice => IsRussian(voice) && voice.Gender == VoiceGender.Male)
                       ?? voices.FirstOrDefault(voice => voice.Gender == VoiceGender.Male)
                       ?? voices.FirstOrDefault(IsRussian)
                       ?? voices[0];

        synthesizer.SelectVoice(selected.Name);
        synthesizer.SetOutputToDefaultAudioDevice();
        synthesizer.Rate = -1;
        synthesizer.Volume = 95;

        var phrase = IsRussian(selected) ? "Нексус Монах" : "Nexus Monach";
        WriteDiagnostic(
            $"Speaking with System.Speech voice '{selected.Name}', culture={selected.Culture.Name}, " +
            $"gender={selected.Gender}, output=default.");
        synthesizer.Speak(phrase);
    }

    private static bool IsRussian(VoiceInfo voice) =>
        voice.Culture.Name.StartsWith("ru", StringComparison.OrdinalIgnoreCase);

    private static void SpeakWithSapiFallback()
    {
        object? voiceObject = null;
        try
        {
            var sapiType = Type.GetTypeFromProgID("SAPI.SpVoice");
            if (sapiType is null) throw new InvalidOperationException("Windows SAPI.SpVoice не зарегистрирован.");
            voiceObject = Activator.CreateInstance(sapiType)
                          ?? throw new InvalidOperationException("Windows не создал SAPI.SpVoice.");
            dynamic voice = voiceObject;

            dynamic russianMaleVoices = voice.GetVoices("Language=419;Gender=Male", "");
            dynamic russianVoices = voice.GetVoices("Language=419", "");
            dynamic maleVoices = voice.GetVoices("Gender=Male", "");
            dynamic allVoices = voice.GetVoices("", "");

            var phrase = "Нексус Монах";
            if (russianMaleVoices.Count > 0)
                voice.Voice = russianMaleVoices.Item(0);
            else if (maleVoices.Count > 0)
            {
                voice.Voice = maleVoices.Item(0);
                phrase = "Nexus Monach";
            }
            else if (russianVoices.Count > 0)
                voice.Voice = russianVoices.Item(0);
            else if (allVoices.Count > 0)
            {
                voice.Voice = allVoices.Item(0);
                phrase = "Nexus Monach";
            }
            else
                throw new InvalidOperationException("Windows SAPI не вернул доступных голосов.");

            dynamic audioOutputs = voice.GetAudioOutputs("", "");
            if (audioOutputs.Count > 0)
                voice.AudioOutput = audioOutputs.Item(0);

            voice.Rate = -1;
            voice.Volume = 95;
            WriteDiagnostic($"Speaking with SAPI fallback; phrase='{phrase}', output=default.");
            voice.Speak(phrase, 0);
            voice.WaitUntilDone(-1);
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
