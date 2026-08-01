namespace NexusMonach.Services;

/// <summary>
/// Проверяемые границы закадрового перевода. Основной HTML5-путь не останавливает
/// видео; очередь короткая и вытесняет устаревшую речь вместо роста задержки.
/// Пауза разрешена только резервному loopback-пути, где иначе SAPI попадёт в
/// собственный вход Whisper.
/// </summary>
internal static class VideoDubbingPolicy
{
    public const int DirectSegmentSeconds = 3;
    public const int MaxBufferedSegments = 2;
    public const int SpeechRate = 1;
    public const double OriginalVolume = 0.22;
    public const bool UsesDomSubtitles = false;

    public static bool ShouldPausePlayback(bool directMediaCaptureAvailable) =>
        !directMediaCaptureAvailable;
}
