namespace NexusMonach.Models;

public sealed class BrowserSession
{
    public List<string> Urls { get; set; } = [];
    public int ActiveIndex { get; set; }
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
}
