using NexusMonach.Intelligence;
using NexusMonach.Intelligence.Fabric;

namespace NexusMonach.Services;

/// <summary>
/// Единая точка жизненного цикла открытого локального Fabric-модуля.
/// Интерфейс сохранён, чтобы оркестрацию можно было тестировать и развивать отдельно.
/// </summary>
public static class NexusFabricRuntime
{
    private static readonly object Sync = new();
    private static INexusIntelligenceFabric? _provider;
    private static NexusFabricStatus _status = Unavailable("Открытый Fabric ещё не инициализирован.");

    public static NexusFabricStatus Status
    {
        get { lock (Sync) return _status; }
    }

    public static bool IsAvailable => Status.IsAvailable;

    public static string ModelRoutingSummary =>
        "Fabric Router · " +
        $"Qwen: анализ {(AiModelCatalog.TextReady ? "✓" : "—")} · " +
        $"OPUS: перевод {(AiModelCatalog.TranslationReady ? "✓" : "—")} · " +
        $"Whisper: речь {(AiModelCatalog.SpeechReady ? "✓" : "—")} · " +
        $"SmolVLM: фото {(AiModelCatalog.VisionReady ? "✓" : "—")} · " +
        $"E5: смысл {(AiModelCatalog.SemanticReady ? "✓" : "—")} · по требованию";

    public static async Task<string> AskTextAsync(string systemPrompt, string userPrompt,
        CancellationToken cancellationToken = default)
    {
        var model = await LocalAiService.GetPreferredModelAsync(cancellationToken)
                    ?? throw new InvalidOperationException(AiModelCatalog.MissingTextRuntimeMessage);
        return await LocalAiService.AskAsync(model, systemPrompt, userPrompt, cancellationToken);
    }

    public static Task<string> UnderstandImageAsync(byte[] image,
        CancellationToken cancellationToken = default) =>
        LocalAiService.DescribeImageForSearchAsync(AiModelCatalog.VisionModelId, image, cancellationToken);

    public static Task<string> TranscribeSpeechAsync(byte[] wav,
        CancellationToken cancellationToken = default) =>
        WhisperService.TranscribeAsync(wav, cancellationToken);

    public static Task<WhisperTranscript> TranscribeSpeechDetailedAsync(byte[] wav,
        CancellationToken cancellationToken = default) =>
        WhisperService.TranscribeDetailedAsync(wav, cancellationToken);

    public static Task<string> TranscribeSpeechToEnglishAsync(byte[] wav,
        CancellationToken cancellationToken = default) =>
        WhisperService.TranscribeToEnglishAsync(wav, cancellationToken);

    public static Task<List<float>> EmbedSemanticsAsync(string text,
        CancellationToken cancellationToken = default) =>
        SemanticEmbeddingService.EmbedAsync(text, cancellationToken);

    public static void Initialize()
    {
        lock (Sync)
        {
            if (_provider is not null) return;

            try
            {
                _provider = new NexusIntelligenceFabric();
                _provider.Initialize(new NexusFabricHostAdapter());
                _status = _provider.GetStatus();
            }
            catch (Exception ex)
            {
                try { _provider?.Dispose(); } catch { }
                _provider = null;
                _status = Unavailable("Ошибка запуска открытого Fabric: " + ex.Message);
            }
        }
    }

    public static async Task<NexusFabricResponse> ExecuteAsync(
        NexusFabricRequest request,
        CancellationToken cancellationToken = default)
    {
        INexusIntelligenceFabric? provider;
        lock (Sync) provider = _provider;
        if (provider is null)
            return new NexusFabricResponse(false, "{}", Status.Message, request.CorrelationId);

        try
        {
            return await provider.ExecuteAsync(request, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new NexusFabricResponse(false, "{}", "Fabric: " + ex.Message, request.CorrelationId);
        }
    }

    public static void Shutdown()
    {
        lock (Sync)
        {
            try { _provider?.Dispose(); } catch { }
            _provider = null;
            _status = Unavailable("Открытый Fabric остановлен.");
        }
    }

    private static NexusFabricStatus Unavailable(string message) =>
        new(false, "Nexus Intelligence Fabric", "—", [], message);
}
