namespace NexusMonach.Models;

/// <summary>
/// Ограниченный, очищенный снимок структуры текущей страницы. Значения форм,
/// cookies, storage, script/style и содержимое скрытых элементов сюда не входят.
/// </summary>
public sealed class SiteTreeSnapshot
{
    public string PageTitle { get; set; } = string.Empty;
    public string Origin { get; set; } = string.Empty;
    public int TotalElements { get; set; }
    public bool Truncated { get; set; }
    public List<SiteTreeItem> Structure { get; set; } = [];
    public List<SiteTreeItem> Links { get; set; } = [];
    public List<SiteTreeItem> Resources { get; set; } = [];
}

public sealed class SiteTreeItem
{
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string CopyValue { get; set; } = string.Empty;
    public List<SiteTreeItem> Children { get; set; } = [];
}
