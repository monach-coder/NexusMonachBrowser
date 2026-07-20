using Microsoft.Web.WebView2.Core;
using NexusMonach.Models;

namespace NexusMonach.Services;

public static class BrowserEnvironment
{
    private static CoreWebView2Environment? _environment;
    private static readonly Dictionary<string, CoreWebView2Profile> Profiles = new(StringComparer.OrdinalIgnoreCase);
    public static bool ExtensionsEnabledAtStartup { get; private set; }
    public static CoreWebView2Environment Current =>
        _environment ?? throw new InvalidOperationException("Среда браузера ещё не создана.");

    public static async Task InitializeAsync()
    {
        if (_environment is not null)
            return;

        try
        {
            _ = CoreWebView2Environment.GetAvailableBrowserVersionString();
        }
        catch (WebView2RuntimeNotFoundException)
        {
            throw new InvalidOperationException(
                "Не найден Microsoft Edge WebView2 Runtime. Установите Evergreen Runtime с официального сайта Microsoft.");
        }

        ExtensionsEnabledAtStartup = SettingsService.Current.EnableExtensions && !GuardianRuntime.IsSafeMode;
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = ProxyConfigurationService.BuildBrowserArguments(SettingsService.Current),
            Language = "ru-RU",
            AreBrowserExtensionsEnabled = ExtensionsEnabledAtStartup,
            EnableTrackingPrevention = true,
            AllowSingleSignOnUsingOSPrimaryAccount = false,
            IsCustomCrashReportingEnabled = true
        };

        _environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: AppPaths.UserData,
            options: options);
        WebView2RuntimeMonitor.Observe(_environment);
    }

    public static CoreWebView2ControllerOptions CreateControllerOptions(bool isPrivate)
    {
        var options = Current.CreateCoreWebView2ControllerOptions();
        options.ProfileName = isPrivate ? "Private" : "Default";
        options.IsInPrivateModeEnabled = isPrivate;
        return options;
    }

    public static CoreWebView2ControllerOptions CreateDiagnosticsControllerOptions()
    {
        var options = Current.CreateCoreWebView2ControllerOptions();
        options.ProfileName = "PrivacyDiagnostics";
        options.IsInPrivateModeEnabled = true;
        return options;
    }

    public static void ApplyPrivacyLevel(CoreWebView2Profile profile, PrivacyLevel level)
    {
        profile.PreferredTrackingPreventionLevel = level switch
        {
            PrivacyLevel.Basic => CoreWebView2TrackingPreventionLevel.Basic,
            PrivacyLevel.Strict => CoreWebView2TrackingPreventionLevel.Strict,
            _ => CoreWebView2TrackingPreventionLevel.Balanced
        };
    }

    public static void RegisterProfile(CoreWebView2Profile profile)
    {
        var key = profile.ProfilePath + "|" + profile.ProfileName;
        Profiles[key] = profile;
    }

    public static async Task ClearBrowsingDataAsync()
    {
        foreach (var profile in Profiles.Values.Distinct().ToList())
        {
            try { await profile.ClearBrowsingDataAsync(); }
            catch { /* Профиль мог быть уже закрыт вместе с последней вкладкой. */ }
        }
    }
}
