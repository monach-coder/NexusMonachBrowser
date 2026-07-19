namespace NexusMonach.Services;

public static class GuardianRuntime
{
    public static string SessionId { get; } = Environment.GetEnvironmentVariable("NEXUS_GUARDIAN_SESSION") ?? string.Empty;
    public static string IntegrityStatus { get; } = Environment.GetEnvironmentVariable("NEXUS_INTEGRITY_STATUS") ?? "not-launched-by-guardian";
    public static bool IsSafeMode { get; } = Environment.GetEnvironmentVariable("NEXUS_SAFE_MODE") == "1";
    public static bool IsGuardianLaunch => !string.IsNullOrWhiteSpace(SessionId);
}
