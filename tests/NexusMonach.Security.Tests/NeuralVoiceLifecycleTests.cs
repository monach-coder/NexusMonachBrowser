using NexusMonach.Services;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace NexusMonach.Security.Tests;

public sealed class NeuralVoiceLifecycleTests
{
    [Fact]
    public void StopIsSafeAndIdempotentWithoutInstalledVoicePack()
    {
        NeuralVoiceService.Stop();
        NeuralVoiceService.Stop();
    }

    [Fact]
    public void BrowserVoiceIsPinnedToLocalSileroKseniya()
    {
        Assert.Contains("Kseniya", AiModelCatalog.VoiceModelId,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Vosk", AiModelCatalog.VoiceModelId,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Piper", AiModelCatalog.VoiceModelId,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AssistantAndDubbingUseIndependentWorkerAndRequestLanes()
    {
        Assert.True(NeuralVoiceService.UsesIndependentLaneWorkers);

        NeuralVoiceService.Stop(NeuralVoiceLane.Assistant);
        NeuralVoiceService.Stop(NeuralVoiceLane.Dubbing);
        NeuralVoiceService.Stop(NeuralVoiceLane.Assistant);
        NeuralVoiceService.Stop(NeuralVoiceLane.Dubbing);
    }

    [Fact]
    public void ColdDubbingVoiceUsesReleaseSmokeBudgetThenReturnsToShortRequests()
    {
        Assert.True(NeuralVoiceService.DubbingColdStartBudget >= TimeSpan.FromMinutes(3));
        Assert.True(NeuralVoiceService.WarmRequestBudget < NeuralVoiceService.DubbingColdStartBudget);
        Assert.True(NeuralVoiceService.WarmRequestBudget >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void FailureAfterIntentionalWorkerStopIsNotReported()
    {
        Assert.True(NeuralVoiceService.ShouldReportSynthesisFailure(7, 7));
        Assert.False(NeuralVoiceService.ShouldReportSynthesisFailure(7, 8));
    }

    [Fact]
    public void CommandsAndDubbingUseIndependentWhisperServersAndInferenceGates()
    {
        Assert.True(WhisperService.UsesIndependentRecognitionLanes);
    }

    [Fact]
    public void VideoDubbingCannotFallBackToWindowsSapi()
    {
        var methods = typeof(VideoDubbingVoiceService).GetMethods(
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        Assert.DoesNotContain(methods,
            method => method.Name.Contains("Sapi", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "FullOfflineVoice")]
    public async Task PackedKseniyaWorker_WarmsUpWithReadyWord()
    {
        var root = FindRepositoryRoot();
        var worker = Path.Combine(root, "src", "NexusMonach", "AI", "voice", "silero",
            "nexus-silero-worker.exe");
        var model = Path.Combine(root, "src", "NexusMonach", "AI", "models", "voice",
            "silero-v5-ru", "v5_5_ru.pt");
        var whisper = Path.Combine(root, "src", "NexusMonach", "AI", "whisper", "Release",
            "whisper-cli.exe");
        var whisperModel = Path.Combine(root, "src", "NexusMonach", "AI", "models", "whisper",
            "ggml-base-q5_1.bin");
        if (!File.Exists(worker) || !File.Exists(model)) return;
        // torch внутри воркера открывает модель через ANSI fopen: репозиторий с
        // кириллицей в пути ломает холодный старт, поэтому тест идёт тем же
        // ASCII-зеркалом, что и продакшн-запуск браузера.
        model = AsciiSafeModelCache.EnsureAsciiSafePath(model);

        var output = Path.Combine(Path.GetTempPath(), $"nexus-ready-{Guid.NewGuid():N}.wav");
        try
        {
            var start = new ProcessStartInfo(worker)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            start.ArgumentList.Add("--model");
            start.ArgumentList.Add(model);
            start.ArgumentList.Add("--stdio");
            using var process = Process.Start(start)!;
            var stderr = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                id = "ready-word",
                text = "Перевод готов. Я готов продолжать перевод без задержки видео.",
                output,
                style = "natural",
                rate = 3
            }));
            await process.StandardInput.FlushAsync();
            var reply = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromMinutes(2));
            process.StandardInput.Close();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            _ = await stderr;

            Assert.NotNull(reply);
            using var json = JsonDocument.Parse(reply);
            Assert.True(json.RootElement.GetProperty("ok").GetBoolean(),
                json.RootElement.GetProperty("error").GetString());
            Assert.True(new FileInfo(output).Length > 1_000);

            if (!File.Exists(whisper) || !File.Exists(whisperModel)) return;

            var recognition = new ProcessStartInfo(whisper)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = new UTF8Encoding(false),
                StandardErrorEncoding = new UTF8Encoding(false)
            };
            foreach (var argument in new[] { "-m", whisperModel, "-f", output, "-l", "ru", "-nt", "-np" })
                recognition.ArgumentList.Add(argument);
            using var recognizer = Process.Start(recognition)!;
            var recognized = recognizer.StandardOutput.ReadToEndAsync();
            var recognitionErrors = recognizer.StandardError.ReadToEndAsync();
            await recognizer.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(2));
            Assert.Equal(0, recognizer.ExitCode);
            Assert.Contains("готов", await recognized, StringComparison.OrdinalIgnoreCase);
            _ = await recognitionErrors;
        }
        finally { try { File.Delete(output); } catch { } }
    }

    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "NexusMonach.sln")))
                return current.FullName;
            current = current.Parent;
        }
        throw new DirectoryNotFoundException("NexusMonach.sln was not found.");
    }
}
