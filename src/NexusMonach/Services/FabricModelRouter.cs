namespace NexusMonach.Services;

internal enum FabricWorkload
{
    TextAnalysis,
    PageTranslation,
    SpeechRecognition,
    ImageUnderstanding,
    SemanticEmbedding
}

internal enum FabricModelKind
{
    QwenText,
    OpusTranslation,
    WhisperSpeech,
    SmolVlmVision,
    MultilingualE5
}

internal sealed record FabricModelRoute(FabricModelKind Model, string ModelId, string Label);

/// <summary>
/// Единый проверяемый контракт маршрутизации локальных задач. Добавление новой
/// задачи без явного выбора модели должно ломать тест, а не молча уходить в Qwen.
/// </summary>
internal static class FabricModelRouter
{
    internal static FabricModelRoute Route(FabricWorkload workload) => workload switch
    {
        FabricWorkload.TextAnalysis => new(FabricModelKind.QwenText, AiModelCatalog.TextModelId, "Qwen"),
        FabricWorkload.PageTranslation => new(FabricModelKind.OpusTranslation, AiModelCatalog.TranslationModelId, "OPUS"),
        FabricWorkload.SpeechRecognition => new(FabricModelKind.WhisperSpeech, AiModelCatalog.SpeechModelId, "Whisper"),
        FabricWorkload.ImageUnderstanding => new(FabricModelKind.SmolVlmVision, AiModelCatalog.VisionModelId, "SmolVLM"),
        FabricWorkload.SemanticEmbedding => new(FabricModelKind.MultilingualE5, AiModelCatalog.SemanticModelId, "E5"),
        _ => throw new ArgumentOutOfRangeException(nameof(workload), workload, "Неизвестный тип локальной задачи.")
    };
}
