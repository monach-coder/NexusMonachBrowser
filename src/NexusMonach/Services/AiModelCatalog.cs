namespace NexusMonach.Services;

/// <summary>
/// Единственная точка, из которой Nexus Monach получает пути к локальным AI-компонентам.
/// Ни один путь из этого каталога не ведёт в профиль пользователя или в сеть.
/// </summary>
public static class AiModelCatalog
{
    public const string TextModelId = "Nexus Fast Intelligence · Qwen3 0.6B";
    public const string SpeechModelId = "Nexus Speech · Whisper base q5_1";
    public const string SemanticModelId = "Nexus Semantics · multilingual-e5-small";
    public const string VisionModelId = "Nexus Vision · SmolVLM 500M";

    public static string Root => Path.Combine(AppContext.BaseDirectory, "AI");
    public static string LlamaRoot => Path.Combine(Root, "llama");
    public static string WhisperRoot => Path.Combine(Root, "whisper");
    public static string TextRoot => Path.Combine(Root, "models", "qwen3-0.6b");
    public static string SpeechRoot => Path.Combine(Root, "models", "whisper");
    public static string SemanticRoot => Path.Combine(Root, "models", "multilingual-e5-small");
    public static string VisionRoot => Path.Combine(Root, "models", "smolvlm-500m");
    public static string NodeRoot => Path.Combine(Root, "node");
    public static string AdapterRoot => Path.Combine(Root, "adapters");

    public static string? LlamaCli => FindFile(LlamaRoot, "llama-cli.exe");
    public static string? VisionCli => FindFile(LlamaRoot, "llama-mtmd-cli.exe");
    public static string? WhisperCli => FindFile(WhisperRoot, "whisper-cli.exe");
    public static string? TextModel => FindFile(TextRoot, "*.gguf");
    public static string? WhisperModel => FindFile(SpeechRoot, "ggml-base-q5_1.bin");
    public static string? VisionModel => FindFile(VisionRoot, "*SmolVLM*Q8_0.gguf");
    public static string? VisionProjector => FindFile(VisionRoot, "mmproj*.gguf");
    public static string? NodeExecutable => FindFile(NodeRoot, "node.exe");
    public static string SemanticAdapter => Path.Combine(AdapterRoot, "semantic.mjs");

    public static bool TextReady => IsUsable(LlamaCli, 8_000) && IsUsable(TextModel, 300_000_000);
    public static bool SpeechReady => IsUsable(WhisperCli, 8_000) && IsUsable(WhisperModel, 50_000_000);
    public static bool SemanticReady => IsUsable(NodeExecutable, 20_000_000) && File.Exists(SemanticAdapter) &&
        Directory.Exists(SemanticRoot) && Directory.EnumerateFiles(SemanticRoot, "*.onnx", SearchOption.AllDirectories).Any(IsUsableModel);
    public static bool VisionReady => IsUsable(VisionCli, 8_000) && IsUsable(VisionModel, 300_000_000) &&
        IsUsable(VisionProjector, 50_000_000);

    public static string ReadinessSummary =>
        $"Текст {(TextReady ? "✓" : "—")} · Речь {(SpeechReady ? "✓" : "—")} · " +
        $"Семантика {(SemanticReady ? "✓" : "—")} · Зрение {(VisionReady ? "✓" : "—")}";

    public static string MissingTextRuntimeMessage =>
        "В этой сборке отсутствует автономный текстовый AI-комплект. Ожидаются AI\\llama\\llama-cli.exe " +
        "и AI\\models\\qwen3-0.6b\\*.gguf. Nexus не загружает модели из сети во время работы.";

    public static string MissingSpeechRuntimeMessage =>
        "В этой сборке отсутствует автономный Whisper-комплект. Ожидаются AI\\whisper\\whisper-cli.exe " +
        "и AI\\models\\whisper\\ggml-base-q5_1.bin. Nexus не загружает модели из сети во время работы.";

    private static string? FindFile(string root, string pattern) => Directory.Exists(root)
        ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).FirstOrDefault()
        : null;

    private static bool IsUsable(string? path, long minimumLength) =>
        path is not null && File.Exists(path) && new FileInfo(path).Length >= minimumLength;

    private static bool IsUsableModel(string path) => new FileInfo(path).Length >= 1_000_000;
}
