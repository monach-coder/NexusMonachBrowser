using System.Diagnostics;

namespace NexusMonach.Services;

/// <summary>Полностью автономное распознавание речи. Сетевых путей установки здесь намеренно нет.</summary>
public static class WhisperService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static string Status { get; private set; } = "Проверка встроенного Whisper";
    public static string? LastError { get; private set; }
    public static bool IsReady => AiModelCatalog.SpeechReady;

    public static void PrepareInBackground()
    {
        Status = IsReady ? "Whisper готов" : "Whisper отсутствует в автономном комплекте";
        LastError = IsReady ? null : AiModelCatalog.MissingSpeechRuntimeMessage;
    }

    public static Task EnsureInstalledAsync(IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsReady)
        {
            LastError = AiModelCatalog.MissingSpeechRuntimeMessage;
            Status = LastError;
            progress?.Report(Status);
            throw new InvalidOperationException(LastError);
        }
        LastError = null;
        Status = "Whisper готов";
        progress?.Report(Status);
        return Task.CompletedTask;
    }

    public static async Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken = default)
    {
        await EnsureInstalledAsync(cancellationToken: cancellationToken);
        await Gate.WaitAsync(cancellationToken);
        var work = Path.Combine(Path.GetTempPath(), "NexusMonachWhisper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var input = Path.Combine(work, "audio.wav");
        var outputBase = Path.Combine(work, "transcript");
        await File.WriteAllBytesAsync(input, wav, cancellationToken);
        try
        {
            var executable = AiModelCatalog.WhisperCli!;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            foreach (var argument in new[]
                     {
                         "-m", AiModelCatalog.WhisperModel!, "-f", input, "-l", "auto",
                         "-otxt", "-of", outputBase, "-nt"
                     })
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start)
                                ?? throw new InvalidOperationException("Не удалось запустить встроенный Whisper.");
            var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
            try { await process.WaitForExitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                throw;
            }
            var log = (await stdout) + "\n" + (await stderr);
            var output = outputBase + ".txt";
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                log = log.Trim();
                throw new InvalidOperationException("Whisper завершился с ошибкой: " +
                                                    log[..Math.Min(log.Length, 700)]);
            }
            return (await File.ReadAllTextAsync(output, cancellationToken)).Trim();
        }
        finally
        {
            Gate.Release();
            try { Directory.Delete(work, recursive: true); } catch { }
        }
    }
}
