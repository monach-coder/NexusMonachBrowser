using System.Diagnostics;
using System.Text.Json;

namespace NexusMonach.Services;

public static class SemanticEmbeddingService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Process? _process;

    public static async Task<List<float>> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        if (!AiModelCatalog.SemanticReady || string.IsNullOrWhiteSpace(text)) return [];
        await Gate.WaitAsync(cancellationToken);
        try
        {
            EnsureProcess();
            var request = JsonSerializer.Serialize(new { text = text[..Math.Min(text.Length, 12_000)] });
            await _process!.StandardInput.WriteLineAsync(request.AsMemory(), cancellationToken);
            await _process.StandardInput.FlushAsync(cancellationToken);
            var line = await _process.StandardOutput.ReadLineAsync(cancellationToken);
            return string.IsNullOrWhiteSpace(line)
                ? []
                : JsonSerializer.Deserialize<List<float>>(line) ?? [];
        }
        catch { Stop(); return []; }
        finally { Gate.Release(); }
    }

    private static void EnsureProcess()
    {
        if (_process is { HasExited: false }) return;
        Stop();
        var start = new ProcessStartInfo(AiModelCatalog.NodeExecutable!)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, WorkingDirectory = AiModelCatalog.Root
        };
        start.ArgumentList.Add(AiModelCatalog.SemanticAdapter);
        start.ArgumentList.Add(AiModelCatalog.SemanticRoot);
        _process = Process.Start(start) ?? throw new InvalidOperationException("Не удалось запустить Nexus Semantics.");
    }

    public static void Stop()
    {
        try { if (_process is { HasExited: false }) _process.Kill(true); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;
    }
}
