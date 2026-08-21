using NexusMonach.Models;

namespace NexusMonach.Services;

internal sealed record VideoDubbingModeProfile(
    VideoTranslationMode Mode,
    int SegmentMilliseconds,
    int SegmentOverlapMilliseconds,
    int ContextSeconds,
    int ContextPhrases,
    int MaximumPendingParts,
    int MaximumPendingWords,
    int MaximumPendingCharacters,
    int StartupPreparedPhrases,
    double StartupPreparedSeconds,
    int StartupMaximumWaitMilliseconds,
    int RefillWaitMilliseconds,
    int PreparedQueueCapacity,
    int MaximumTtsCharacters,
    double MaximumPreparedAudioSeconds,
    double MaximumBufferedAudioSeconds);

/// <summary>
/// Проверяемые границы закадрового перевода. Ни один путь захвата или озвучивания
/// не имеет права останавливать либо перематывать пользовательское видео.
/// </summary>
internal static class VideoDubbingPolicy
{
    public const double ShortClipMaximumSeconds = 5 * 60;
    public const double LongFormMinimumSeconds = 30 * 60;
    public const int SegmentMilliseconds = 3_200;
    public const int SegmentOverlapMilliseconds = 800;
    public const int MaxBufferedSegments = 6;
    public const int MaxSegmentAgeMilliseconds = 12_000;
    public const int PlaybackProbeMilliseconds = 180;
    public const int DirectSilenceProbeLimit = 3;
    public const int FirstLoopbackSegmentTimeoutMilliseconds = 12_000;
    /// <summary>
    /// Сколько реплик подряд может не справиться локальный голос, прежде чем
    /// сессия закадрового перевода признаётся неработоспособной. Одна-две
    /// случайные сбоя не должны глушить весь перевод: реплика пропускается,
    /// видео и очередь продолжаются.
    /// </summary>
    public const int MaxConsecutivePlaybackFailures = 4;
    public const double OriginalVolume = 0.12;
    public const double MinimumAudibleRms = 0.00018;
    public const double MinimumAudiblePeak = 0.0015;
    public const double TargetRecognitionRms = 0.055;
    public const double MaximumRecognitionGain = 12.0;
    public const bool UsesDomSubtitles = false;

    public static VideoDubbingModeProfile ForMode(VideoTranslationMode mode) => mode switch
    {
        VideoTranslationMode.Fast => new(mode,
            SegmentMilliseconds: 3_000,
            SegmentOverlapMilliseconds: 600,
            ContextSeconds: 60,
            ContextPhrases: 6,
            MaximumPendingParts: 2,
            MaximumPendingWords: 14,
            MaximumPendingCharacters: 110,
            StartupPreparedPhrases: 1,
            StartupPreparedSeconds: 0.35,
            StartupMaximumWaitMilliseconds: 3_000,
            RefillWaitMilliseconds: 300,
            PreparedQueueCapacity: 4,
            MaximumTtsCharacters: 110,
            MaximumPreparedAudioSeconds: 9,
            MaximumBufferedAudioSeconds: 10),
        VideoTranslationMode.Quality => new(mode,
            SegmentMilliseconds: 4_000,
            SegmentOverlapMilliseconds: 1_000,
            ContextSeconds: 90,
            ContextPhrases: 12,
            MaximumPendingParts: 4,
            MaximumPendingWords: 28,
            MaximumPendingCharacters: 210,
            StartupPreparedPhrases: 1,
            StartupPreparedSeconds: 0.65,
            StartupMaximumWaitMilliseconds: 4_500,
            RefillWaitMilliseconds: 650,
            PreparedQueueCapacity: 8,
            MaximumTtsCharacters: 140,
            MaximumPreparedAudioSeconds: 12,
            MaximumBufferedAudioSeconds: 22),
        _ => new(VideoTranslationMode.Balanced,
            SegmentMilliseconds: SegmentMilliseconds,
            SegmentOverlapMilliseconds: SegmentOverlapMilliseconds,
            ContextSeconds: 75,
            ContextPhrases: 8,
            MaximumPendingParts: 3,
            MaximumPendingWords: 20,
            MaximumPendingCharacters: 150,
            StartupPreparedPhrases: 1,
            StartupPreparedSeconds: 0.5,
            StartupMaximumWaitMilliseconds: 3_500,
            RefillWaitMilliseconds: 450,
            PreparedQueueCapacity: 6,
            MaximumTtsCharacters: 110,
            MaximumPreparedAudioSeconds: 9,
            MaximumBufferedAudioSeconds: 15)
    };

    /// <summary>
    /// Balanced is the automatic everyday mode: short clips prioritize first
    /// speech latency, while feature-length material gets a deeper context and
    /// prepared reserve. Explicit Fast/Quality choices are never overridden.
    /// </summary>
    public static VideoTranslationMode SelectEffectiveMode(VideoTranslationMode configuredMode,
        double? durationSeconds)
    {
        if (configuredMode != VideoTranslationMode.Balanced ||
            durationSeconds is null || !double.IsFinite(durationSeconds.Value) ||
            durationSeconds <= 0)
            return configuredMode;
        if (durationSeconds <= ShortClipMaximumSeconds) return VideoTranslationMode.Fast;
        if (durationSeconds >= LongFormMinimumSeconds) return VideoTranslationMode.Quality;
        return VideoTranslationMode.Balanced;
    }

    public static string PrepareTtsText(string? text, VideoDubbingModeProfile profile,
        int? maximumCharacters = null)
    {
        text = VoiceAssistantService.SanitizeForSpeech(text);
        var limit = Math.Clamp(maximumCharacters ?? profile.MaximumTtsCharacters, 40, 240);
        if (text.Length <= limit) return text;
        var minimum = Math.Max(20, limit / 2);
        var split = -1;
        for (var index = Math.Min(limit, text.Length - 1); index >= minimum; index--)
        {
            if (text[index] is '.' or '!' or '?' or '…' or ';' or ':' ||
                char.IsWhiteSpace(text[index]))
            {
                split = index + (char.IsWhiteSpace(text[index]) ? 0 : 1);
                break;
            }
        }
        if (split < minimum) split = limit;
        return text[..split].TrimEnd(' ', ',', ';', ':', '-', '—') + "…";
    }

    /// <summary>
    /// Делит длинный перевод на короткие реплики для синтеза. Обрезка с «…»
    /// теряла смысл хвоста фразы; деление сохраняет всё содержимое целиком —
    /// части произносятся подряд с естественной паузой на границе.
    /// </summary>
    public static IReadOnlyList<string> SplitTtsText(string? text,
        VideoDubbingModeProfile profile, int? maximumCharacters = null)
    {
        var normalized = VoiceAssistantService.SanitizeForSpeech(text);
        if (normalized.Length == 0) return [];
        var limit = Math.Clamp(maximumCharacters ?? profile.MaximumTtsCharacters, 40, 240);
        if (normalized.Length <= limit) return [normalized];

        var chunks = new List<string>();
        var remaining = normalized;
        while (remaining.Length > limit)
        {
            var split = FindSplitIndex(remaining, limit);
            var chunk = remaining[..split].TrimEnd(' ', ',', ';', ':', '-', '—');
            if (chunk.Length > 0) chunks.Add(chunk);
            remaining = remaining[split..].TrimStart();
        }
        if (remaining.Length > 0) chunks.Add(remaining);
        return chunks;
    }

    private static int FindSplitIndex(string text, int limit)
    {
        var minimum = Math.Max(20, limit / 2);
        var bestSentence = -1;
        var bestSpace = -1;
        for (var index = Math.Min(limit, text.Length - 1); index >= minimum; index--)
        {
            var isBoundary = text[index] is '.' or '!' or '?' or '…';
            if (isBoundary && bestSentence < 0) bestSentence = index + 1;
            if (text[index] is ';' or ':' && bestSentence < 0 && bestSpace < 0) bestSentence = index + 1;
            if (char.IsWhiteSpace(text[index]) && bestSpace < 0) bestSpace = index;
            if (bestSentence >= 0) break;
        }
        if (bestSentence >= minimum) return bestSentence;
        if (bestSpace >= minimum) return bestSpace;
        return limit;
    }

    public static bool IsPreparedAudioAcceptable(TimeSpan duration,
        VideoDubbingModeProfile profile) =>
        duration > TimeSpan.Zero && duration.TotalSeconds <= profile.MaximumPreparedAudioSeconds;

    /// <summary>
    /// Русская озвучка обычно длиннее исходной речи, поэтому без сброса
    /// накопленный буфер растёт неограниченно: задержка догоняет минуты, а
    /// после остановки видео очередь продолжает говорить. Когда озвученного
    /// запаса слишком много, новые реплики не ставятся в очередь — перевод
    /// возвращается к синхрону, пропустив отставший хвост.
    /// </summary>
    public static bool ShouldShedTranslation(double bufferedAudioSeconds,
        VideoDubbingModeProfile profile) =>
        bufferedAudioSeconds >= profile.MaximumBufferedAudioSeconds;

    public static bool ShouldFinalizeUtterance(string? text, int fragmentCount,
        VideoDubbingModeProfile profile)
    {
        text = WhisperService.NormalizeTranscript(text);
        if (text.Length == 0) return false;
        var beginsLikeContinuation = text.Length > 1 &&
                                     char.IsLower(text[0]) &&
                                     char.IsLower(text[1]);
        if (!beginsLikeContinuation &&
            System.Text.RegularExpressions.Regex.IsMatch(text, @"[.!?…][""'»)]?$"))
            return true;
        var wordCount = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return wordCount >= profile.MaximumPendingWords ||
               text.Length >= profile.MaximumPendingCharacters ||
               fragmentCount >= profile.MaximumPendingParts;
    }

    public static bool ShouldPausePlayback(bool directMediaCaptureAvailable) => false;

    public static bool SupportsProcessLoopback(Version windowsVersion, int targetProcessId) =>
        targetProcessId > 0 && windowsVersion >= new Version(10, 0, 20348);

    public static bool IsFresh(DateTimeOffset capturedAt, DateTimeOffset now) =>
        capturedAt <= now && now - capturedAt <= TimeSpan.FromMilliseconds(MaxSegmentAgeMilliseconds);

    public static bool HasUsableDirectAudio(bool success, string? wavBase64) =>
        success && !string.IsNullOrWhiteSpace(wavBase64);

    public static bool IsSilentDirectCapture(bool success, string? wavBase64) =>
        success && string.IsNullOrWhiteSpace(wavBase64);

    public static bool IsAudible(double rms, double peak) =>
        rms >= MinimumAudibleRms || peak >= MinimumAudiblePeak;

    public static double SelectRecognitionGain(double rms, double peak)
    {
        if (!IsAudible(rms, peak) || rms <= 0 || peak <= 0) return 1;
        var rmsGain = TargetRecognitionRms / rms;
        var clippingGain = 0.94 / peak;
        return Math.Clamp(Math.Min(rmsGain, clippingGain), 1, MaximumRecognitionGain);
    }

    // Live dubbing always keeps Kseniya's native tempo. Artificial speed-up
    // makes consonants and short words indistinct and does not solve backlog.
    public static int SelectSpeechRate(int characterCount) => 0;
}
