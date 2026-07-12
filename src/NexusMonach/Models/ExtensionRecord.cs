namespace NexusMonach.Models;

public sealed class ExtensionRecord
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string ManagedPath { get; set; } = string.Empty;
    public string SourceFingerprint { get; set; } = string.Empty;
    public DateTime InstalledAtUtc { get; set; } = DateTime.UtcNow;
}
