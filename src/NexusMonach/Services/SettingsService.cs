using NexusMonach.Models;

namespace NexusMonach.Services;

public static class SettingsService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static BrowserSettings Current { get; private set; } = new();

    public static async Task InitializeAsync()
    {
        Current = await JsonStore.ReadAsync<BrowserSettings>(AppPaths.SettingsFile) ?? new BrowserSettings();
        await SaveAsync(Current);
    }

    public static async Task SaveAsync(BrowserSettings settings)
    {
        await Gate.WaitAsync();
        try
        {
            Current = settings.Clone();
            await JsonStore.WriteAsync(AppPaths.SettingsFile, Current);
        }
        finally
        {
            Gate.Release();
        }
    }
}
