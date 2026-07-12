using NexusMonach.Models;

namespace NexusMonach.Services;

public static class SessionService
{
    public static async Task<BrowserSession?> LoadAsync()
    {
        if (!SettingsService.Current.RestoreSession)
            return null;
        var session = await JsonStore.ReadAsync<BrowserSession>(AppPaths.SessionFile);
        if (session is null || session.Urls.Count == 0)
            return null;
        session.Urls = session.Urls.Where(x => !string.IsNullOrWhiteSpace(x)).Take(20).ToList();
        session.ActiveIndex = Math.Clamp(session.ActiveIndex, 0, Math.Max(0, session.Urls.Count - 1));
        return session;
    }

    public static Task SaveAsync(IEnumerable<string> urls, int activeIndex) =>
        JsonStore.WriteAsync(AppPaths.SessionFile, new BrowserSession
        {
            Urls = urls.Where(x => !string.IsNullOrWhiteSpace(x)).Take(20).ToList(),
            ActiveIndex = activeIndex,
            SavedAtUtc = DateTime.UtcNow
        });
}
