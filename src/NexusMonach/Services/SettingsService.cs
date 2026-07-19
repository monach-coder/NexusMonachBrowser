using NexusMonach.Models;

namespace NexusMonach.Services;

public static class SettingsService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static BrowserSettings Current { get; private set; } = new();

    public static async Task InitializeAsync()
    {
        var stored = await JsonStore.ReadAsync<BrowserSettings>(AppPaths.SettingsFile);
        Current = stored ?? new BrowserSettings();
        if (Current.CrashReportDestination == CrashReportDestination.HttpsCollector &&
            string.IsNullOrWhiteSpace(Current.CrashReportEndpoint) &&
            Uri.TryCreate(GuardianReportingDefaults.Endpoint, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps)
        {
            Current.CrashReportDestination = CrashReportDestination.HttpsCollector;
            Current.CrashReportEndpoint = endpoint.AbsoluteUri;
        }
        if (stored is null && GuardianReportingDefaults.Mode.Equals("automatic", StringComparison.OrdinalIgnoreCase))
            Current.CrashReportMode = CrashReportMode.AutomaticAnonymous;
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
