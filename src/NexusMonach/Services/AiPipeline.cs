namespace NexusMonach.Services;

/// <summary>
/// Швы AI-конвейера: интерфейсы распознавания, перевода и озвучки.
/// Продакшн-реализации делегируют локальным моделям; тесты подменяют
/// фейками без процессов и моделей, а будущий облачный переводчик
/// встаёт в то же гнездо без правки вызывающего кода.
/// </summary>
public static class AiPipeline
{
    /// <summary>Распознавание речи (Whisper) из WAV-фрагмента.</summary>
    public interface ISpeechRecognizer
    {
        Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken = default);
    }

    /// <summary>Перевод текста на русский (локальный OPUS или будущий облачный бэкенд).</summary>
    public interface ITextTranslator
    {
        Task<string> TranslateToRussianAsync(string text, bool sourceIsEnglish = false,
            CancellationToken cancellationToken = default, string? sourceLanguage = null);
    }

    /// <summary>Голосовое оповещение пользователя — фирменная функция браузера.</summary>
    public interface IVoiceAnnouncer
    {
        bool Announce(string text,
            VoiceAnnouncementPriority priority = VoiceAnnouncementPriority.Important,
            bool isPrivateWindow = false);
    }

    private sealed class LocalRecognizer : ISpeechRecognizer
    {
        public Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken = default) =>
            WhisperService.TranscribeAsync(wav, cancellationToken);
    }

    private sealed class LocalTranslator : ITextTranslator
    {
        public Task<string> TranslateToRussianAsync(string text, bool sourceIsEnglish = false,
            CancellationToken cancellationToken = default, string? sourceLanguage = null) =>
            TranslationService.TranslateToRussianAsync(text, sourceIsEnglish, cancellationToken, sourceLanguage);
    }

    private sealed class LocalVoice : IVoiceAnnouncer
    {
        public bool Announce(string text,
            VoiceAnnouncementPriority priority = VoiceAnnouncementPriority.Important,
            bool isPrivateWindow = false) =>
            VoiceAssistantService.Announce(text, priority, isPrivateWindow);
    }

    /// <summary>Текущие реализации; заменяются в тестах и при смене бэкенда.</summary>
    public static ISpeechRecognizer Recognizer { get; set; } = new LocalRecognizer();
    public static ITextTranslator Translator { get; set; } = new LocalTranslator();
    public static IVoiceAnnouncer Voice { get; set; } = new LocalVoice();
}
