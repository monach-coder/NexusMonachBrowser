using Microsoft.Web.WebView2.Core;
using NexusMonach.Models;

namespace NexusMonach.Services;

public sealed record WebRequestObservation(
    string Url,
    bool Blocked,
    bool IsKnownTracker,
    string ResourceKind,
    bool HasCookieHeader,
    bool HasReferrerHeader,
    bool HasOriginHeader);

public static class TrackingProtectionService
{
    private static readonly string[] StrictTrackerHosts =
    [
        "doubleclick.net", "google-analytics.com", "googletagmanager.com",
        "googleadservices.com", "connect.facebook.net", "analytics.facebook.com",
        "hotjar.com", "hotjar.io", "clarity.ms", "scorecardresearch.com",
        "segment.com", "segment.io", "mixpanel.com", "amplitude.com",
        "newrelic.com", "nr-data.net", "sentry.io", "quantserve.com",
        "adsrvr.org", "adnxs.com", "criteo.com", "criteo.net",
        "mc.yandex.ru", "metrika.yandex.ru", "top-fwz1.mail.ru", "counter.yadro.ru"
    ];

    public static void Attach(CoreWebView2 core, Func<string?> topLevelUrl, Action onBlocked,
        Action<WebRequestObservation>? onObserved = null, bool forceStrict = false)
    {
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            ApplyPrivacyHeaders(e.Request.Headers, forceStrict);
            var knownTracker = IsKnownTracker(e.Request.Uri);
            var shouldBlock = (forceStrict || SettingsService.Current.PrivacyLevel == PrivacyLevel.Strict) &&
                              e.ResourceContext != CoreWebView2WebResourceContext.Document &&
                              ShouldBlock(e.Request.Uri, topLevelUrl(), knownTracker);
            onObserved?.Invoke(new WebRequestObservation(
                e.Request.Uri,
                shouldBlock,
                knownTracker,
                e.ResourceContext.ToString(),
                HasHeader(e.Request.Headers, "Cookie"),
                HasHeader(e.Request.Headers, "Referer"),
                HasHeader(e.Request.Headers, "Origin")));

            if (!shouldBlock)
                return;

            e.Response = BrowserEnvironment.Current.CreateWebResourceResponse(
                new MemoryStream([]),
                204,
                "No Content",
                "Content-Type: text/plain\r\nCache-Control: no-store");
            onBlocked();
        };
    }

    private static bool HasHeader(CoreWebView2HttpRequestHeaders headers, string name)
    {
        try { return headers.Contains(name); }
        catch { return false; }
    }

    private static void ApplyPrivacyHeaders(CoreWebView2HttpRequestHeaders headers, bool forceStrict)
    {
        try
        {
            if (forceStrict || SettingsService.Current.SendDoNotTrack)
                headers.SetHeader("DNT", "1");
            if (forceStrict || SettingsService.Current.SendGlobalPrivacyControl)
                headers.SetHeader("Sec-GPC", "1");
        }
        catch
        {
            // Некоторые служебные запросы Chromium не разрешают менять заголовки.
        }
    }

    private static bool IsKnownTracker(string requestUrl)
    {
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out var request) ||
            (request.Scheme != Uri.UriSchemeHttp && request.Scheme != Uri.UriSchemeHttps))
            return false;

        return StrictTrackerHosts.Any(x =>
            request.Host.Equals(x, StringComparison.OrdinalIgnoreCase) ||
            request.Host.EndsWith('.' + x, StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldBlock(string requestUrl, string? topLevelUrl, bool knownTracker)
    {
        if (!knownTracker ||
            !Uri.TryCreate(requestUrl, UriKind.Absolute, out var request) ||
            (request.Scheme != Uri.UriSchemeHttp && request.Scheme != Uri.UriSchemeHttps))
            return false;

        if (!Uri.TryCreate(topLevelUrl, UriKind.Absolute, out var top))
            return true;

        if (SiteRuleService.BypassesAdditionalBlocking(top.Host))
            return false;

        return !SameSite(request.Host, top.Host);
    }

    private static bool SameSite(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.EndsWith('.' + right, StringComparison.OrdinalIgnoreCase) ||
        right.EndsWith('.' + left, StringComparison.OrdinalIgnoreCase);
}
