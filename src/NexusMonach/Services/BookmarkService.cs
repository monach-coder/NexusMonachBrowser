using NexusMonach.Models;

namespace NexusMonach.Services;

public static class BookmarkService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<Bookmark> _items = [];

    public static IReadOnlyList<Bookmark> Items => _items
        .OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    public static async Task InitializeAsync() =>
        _items = await JsonStore.ReadAsync<List<Bookmark>>(AppPaths.BookmarksFile) ?? [];

    public static bool Contains(string? url) =>
        !string.IsNullOrWhiteSpace(url) && _items.Any(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));

    public static async Task ToggleAsync(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url) || UrlService.IsInternal(url))
            return;

        await Gate.WaitAsync();
        try
        {
            var existing = _items.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
                _items.Add(new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url });
            else
                _items.Remove(existing);
            await JsonStore.WriteAsync(AppPaths.BookmarksFile, _items);
        }
        finally
        {
            Gate.Release();
        }
    }

    public static async Task RemoveAsync(string url)
    {
        await Gate.WaitAsync();
        try
        {
            _items.RemoveAll(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            await JsonStore.WriteAsync(AppPaths.BookmarksFile, _items);
        }
        finally { Gate.Release(); }
    }
}
