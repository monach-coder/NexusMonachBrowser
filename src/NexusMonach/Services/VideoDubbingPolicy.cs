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
    double MaximumPreparedAudioSeconds);

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
    public const int MaxBufferedSegments = 3;
    public const int MaxSegmentAgeMilliseconds = 9_000;
    public const int PlaybackProbeMilliseconds = 180;
    public const int DirectSilenceProbeLimit = 3;
    public const int FirstLoopbackSegmentTimeoutMilliseconds = 12_000;
    public const double OriginalVolume = 0.12;
    public const double MinimumAudibleRms = 0.00018;
    public const double MinimumAudiblePeak = 0.0015;
    public const double TargetRecognitionRms = 0.055;
    public const double MaximumRecognitionGain = 12.0;
    public const bool UsesDomSubtitles = false;

    public static VideoDubbingModeProfile ForMode(VideoTranslationMode mode) => mode switch
    {
        VideoTranslationMode.Fast => new(mode,
            SegmentMilliseconds: 2_400,
            SegmentOverlapMilliseconds: 500,
            ContextSeconds: 60,
            ContextPhrases: 6,
            MaximumPendingParts: 2,
            MaximumPendingWords: 14,
            MaximumPendingCharacters: 110,
            StartupPreparedPhrases: 1,
            StartupPreparedSeconds: 2.0,
            StartupMaximumWaitMilliseconds: 4_000,
            RefillWaitMilliseconds: 400,
            PreparedQueueCapacity: 4,
            MaximumTtsCharacters: 80,
            MaximumPreparedAudioSeconds: 7),
        VideoTranslationMode.Quality => new(mode,
            SegmentMilliseconds: 4_000,
            SegmentOverlapMilliseconds: 1_000,
            ContextSeconds: 90,
            ContextPhrases: 12,
            MaximumPendingParts: 4,
            MaximumPendingWords: 28,
            MaximumPendingCharacters: 210,
            StartupPreparedPhrases: 3,
            StartupPreparedSeconds: 8.0,
            StartupMaximumWaitMilliseconds: 14_000,
            RefillWaitMilliseconds: 1_800,
            PreparedQueueCapacity: 8,
            MaximumTtsCharacters: 140,
            MaximumPreparedAudioSeconds: 12),
        _ => new(VideoTranslationMode.Balanced,
            SegmentMilliseconds: SegmentMilliseconds,
            SegmentOverlapMilliseconds: SegmentOverlapMilliseconds,
            ContextSeconds: 75,
            ContextPhrases: 8,
            MaximumPendingParts: 3,
            MaximumPendingWords: 20,
            MaximumPendingCharacters: 150,
            StartupPreparedPhrases: 2,
            StartupPreparedSeconds: 5.0,
            StartupMaximumWaitMilliseconds: 9_000,
            RefillWaitMilliseconds: 1_000,
            PreparedQueueCapacity: 6,
            MaximumTtsCharacters: 110,
            MaximumPreparedAudioSeconds: 9)
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

    public static bool IsPreparedAudioAcceptable(TimeSpan duration,
        VideoDubbingModeProfile profile) =>
        duration > TimeSpan.Zero && duration.TotalSeconds <= profile.MaximumPreparedAudioSeconds;

    public static bool ShouldFinalizeUtterance(string? text, int fragmentCount,
        VideoDubbingModeProfile profile)
    {
        text = WhisperService.NormalizeTranscript(text);
        if (text.Length == 0) return false;
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"[.!?…][""'»)]?$"))
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
