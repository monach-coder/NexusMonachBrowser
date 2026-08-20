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
        Current.RestoreSession = false;
        try
        {
            if (File.Exists(AppPaths.SessionFile))
                File.Delete(AppPaths.SessionFile);
        }
        catch
        {
            // Старый session.json не используется и будет удалён при закрытии.
        }
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
        try
        {
            await SaveAsync(Current);
        }
        catch (Exception ex)
        {
            // Another instance may still hold settings.json while this one is
            // starting (safe restart, portable copy). Browsing must start with
            // the loaded in-memory settings; the next successful save persists
            // the normalization.
            CrashReportService.RecordNonFatal("settings", "initial-normalize-persist", ex);
        }
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

    public static async Task<bool> ConsumeInitialProtectionSetupAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (Current.InitialProtectionSetupShown) return false;
            Current.InitialProtectionSetupShown = true;
            await JsonStore.WriteAsync(AppPaths.SettingsFile, Current);
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }
}
