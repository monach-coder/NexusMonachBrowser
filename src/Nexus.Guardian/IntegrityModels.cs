using System.Text.Json.Serialization;

namespace Nexus.Guardian;

internal sealed class IntegrityManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = "Nexus Monach";
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public List<IntegrityFile> Files { get; set; } = [];
}

internal sealed class IntegrityFile
{
    public string Path { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    public bool Critical { get; set; }
    public bool Large { get; set; }
}

internal enum IntegrityState
{
    Verified,
    DevelopmentBuild,
    NonCriticalMismatch,
    CriticalMismatch,
    InvalidSignature
}

internal sealed class IntegrityResult
{
    public IntegrityState State { get; init; }
    public List<string> Problems { get; init; } = [];
    public bool CanLaunch => State is IntegrityState.Verified or IntegrityState.DevelopmentBuild or IntegrityState.NonCriticalMismatch;
    public string CompactStatus => State switch
    {
        IntegrityState.Verified => "verified",
        IntegrityState.DevelopmentBuild => "development-unverified",
        IntegrityState.NonCriticalMismatch => "degraded",
        IntegrityState.CriticalMismatch => "critical-mismatch",
        IntegrityState.InvalidSignature => "invalid-signature",
        _ => "unknown"
    };
}

internal sealed class GuardianCrashState
{
    public List<DateTimeOffset> AbnormalExitsUtc { get; set; } = [];
}

internal sealed class GuardianSessionResult
{
    [JsonPropertyName("sessionId")]
    public string SessionId { get; set; } = string.Empty;

    [JsonPropertyName("cleanExit")]
    public bool CleanExit { get; set; }
}

internal sealed class GuardianIntegrityIncidentState
{
    public string Signature { get; set; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; set; }
}
