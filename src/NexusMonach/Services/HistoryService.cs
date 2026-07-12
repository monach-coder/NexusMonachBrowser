using NexusMonach.Models;

namespace NexusMonach.Services;

public static class HistoryService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<HistoryEntry> _items = [];
    public static IReadOnlyList<HistoryEntry> Items => _items.OrderByDescending(x => x.VisitedAtUtc).ToList();

    public static async Task InitializeAsync() =>
        _items = await JsonStore.ReadAsync<List<HistoryEntry>>(AppPaths.HistoryFile) ?? [];

    public static async Task AddAsync(string title, string url)
    {
        if (!SettingsService.Current.SaveHistory || string.IsNullOrWhiteSpace(url) || UrlService.IsInternal(url))
            return;

        await Gate.WaitAsync();
        try
        {
            var last = _items.LastOrDefault();
            if (last is not null && last.Url.Equals(url, StringComparison.OrdinalIgnoreCase) &&
                DateTime.UtcNow - last.VisitedAtUtc < TimeSpan.FromSeconds(3))
                return;

            _items.Add(new HistoryEntry
            {
                Title = string.IsNullOrWhiteSpace(title) ? url : title,
                Url = url,
                VisitedAtUtc = DateTime.UtcNow
            });
            if (_items.Count > 5000)
                _items.RemoveRange(0, _items.Count - 5000);
            await JsonStore.WriteAsync(AppPaths.HistoryFile, _items);
        }
        finally { Gate.Release(); }
    }

    public static async Task ClearAsync()
    {
        await Gate.WaitAsync();
        try
        {
            _items.Clear();
            await JsonStore.WriteAsync(AppPaths.HistoryFile, _items);
        }
        finally { Gate.Release(); }
    }
}
