namespace NexusMonach.Models;

public sealed class Bookmark
{
    public string Title { get; set; } = "Без названия";
    public string Url { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
