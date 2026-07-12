namespace NexusMonach.Models;

public sealed class HistoryEntry
{
    public string Title { get; set; } = "Без названия";
    public string Url { get; set; } = string.Empty;
    public DateTime VisitedAtUtc { get; set; } = DateTime.UtcNow;
}
