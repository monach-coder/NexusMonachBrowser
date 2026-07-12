namespace NexusMonach.Models;

public sealed class SitePrivacyRule
{
    public string Host { get; set; } = string.Empty;
    public bool BypassAdditionalTrackerBlocking { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
