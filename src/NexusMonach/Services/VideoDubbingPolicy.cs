namespace NexusMonach.Services;

/// <summary>
/// Проверяемые границы закадрового перевода. Ни один путь захвата или озвучивания
/// не имеет права останавливать либо перематывать пользовательское видео.
/// </summary>
internal static class VideoDubbingPolicy
{
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
