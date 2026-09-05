namespace NexusMonach.Services;

public static class GuardianRuntime
{
    public static string SessionId { get; } = Environment.GetEnvironmentVariable("NEXUS_GUARDIAN_SESSION") ?? string.Empty;
    public static string IntegrityStatus { get; } = Environment.GetEnvironmentVariable("NEXUS_INTEGRITY_STATUS") ?? "not-launched-by-guardian";
    public static bool IsSafeMode { get; } = Environment.GetEnvironmentVariable("NEXUS_SAFE_MODE") == "1";

    /// <summary>
    /// Осторожный режим после одиночного сбоя графики: ускорение GPU выключено,
    /// но AI, расширения и голос работают как обычно.
    /// </summary>
    public static bool DisableGpuOnly { get; } =
        Environment.GetEnvironmentVariable("NEXUS_DISABLE_GPU") == "1";

    /// <summary>Версия, до которой Guardian только что обновил браузер.</summary>
    public static string? UpdatedToVersion =>
        Environment.GetEnvironmentVariable("NEXUS_UPDATED_VERSION");

    /// <summary>
    /// Версия, обновление до которой НЕ встало: Guardian откатился на
    /// текущую версию и просит честно об этом сказать.
    /// </summary>
    public static string? UpdateFailedVersion =>
        Environment.GetEnvironmentVariable("NEXUS_UPDATE_FAILED");

    /// <summary>
    /// Краткий вердикт стартовой самодиагностики Guardian («ok»,
    /// «warn:disk,webview2», «fail:integrity») — из последнего отчёта
    /// Guardian\Reports\startup-health-*.json.
    /// </summary>
    public static string StartupHealth { get; } =
        Environment.GetEnvironmentVariable("NEXUS_STARTUP_HEALTH") ?? "unknown";

    public static bool IsGuardianLaunch => !string.IsNullOrWhiteSpace(SessionId);
}
