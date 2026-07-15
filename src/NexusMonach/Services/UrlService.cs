using System.Net;

namespace NexusMonach.Services;

public static class UrlService
{
    public const string NewTabUrl = "https://nexus.local/start.html";

    private static readonly HashSet<string> TrackingKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "fbclid", "gclid", "dclid", "msclkid", "yclid", "ymclid", "_openstat",
        "mc_cid", "mc_eid", "igshid", "vero_conv", "vero_id", "wickedid",
        "rb_clickid", "s_cid", "ref_src", "ref_url"
    };

    public static string Resolve(string input)
    {
        input = (input ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(input) || input.Equals("app://newtab", StringComparison.OrdinalIgnoreCase))
            return NewTabUrl;

        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) &&
            (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
            return CleanTrackingParameters(absolute.AbsoluteUri);

        if (!input.Contains(' ') && LooksLikeHost(input))
        {
            var scheme = IsLocalHost(input) ? "http://" : "https://";
            if (Uri.TryCreate(scheme + input, UriKind.Absolute, out var hostUri))
                return CleanTrackingParameters(hostUri.AbsoluteUri);
        }

        return BuildSearchUrl(input);
    }

    public static string GetHomePage() => Resolve(SettingsService.Current.HomePage);

    public static string CleanTrackingParameters(string url)
    {
        if (!SettingsService.Current.StripTrackingParameters ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrEmpty(uri.Query))
            return url;

        var kept = new List<string>();
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = pair.IndexOf('=');
            var rawKey = separator >= 0 ? pair[..separator] : pair;
            var key = Uri.UnescapeDataString(rawKey.Replace('+', ' '));
            if (key.StartsWith("utm_", StringComparison.OrdinalIgnoreCase) || TrackingKeys.Contains(key))
                continue;
            kept.Add(pair);
        }

        if (kept.Count == uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries).Length)
            return url;

        var builder = new UriBuilder(uri) { Query = string.Join('&', kept) };
        return builder.Uri.AbsoluteUri;
    }

    public static bool IsInternal(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        uri.Host.Equals("nexus.local", StringComparison.OrdinalIgnoreCase);

    public static bool IsSearchQuery(string? input)
    {
        input = input?.Trim();
        if (string.IsNullOrWhiteSpace(input)) return false;
        if (Uri.TryCreate(input, UriKind.Absolute, out var absolute) &&
            absolute.Scheme is "http" or "https" or "file" or "about") return false;
        return input.Contains(' ') || !LooksLikeHost(input);
    }

    private static string BuildSearchUrl(string query)
    {
        var escaped = Uri.EscapeDataString(query);
        return SettingsService.Current.SearchEngine switch
        {
            Models.SearchEngineKind.Brave => $"https://search.brave.com/search?q={escaped}",
            Models.SearchEngineKind.Startpage => $"https://www.startpage.com/sp/search?query={escaped}",
            Models.SearchEngineKind.Google => $"https://www.google.com/search?q={escaped}",
            Models.SearchEngineKind.Yandex => $"https://yandex.ru/search/?text={escaped}",
            _ => $"https://duckduckgo.com/?q={escaped}"
        };
    }

    private static bool LooksLikeHost(string input)
    {
        var host = input.Split('/')[0].Split(':')[0].Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               IPAddress.TryParse(host, out _) ||
               host.Contains('.') && host.All(c => char.IsLetterOrDigit(c) || c is '.' or '-');
    }

    private static bool IsLocalHost(string input)
    {
        var host = input.Split('/')[0].Split(':')[0].Trim('[', ']');
        return host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
               IPAddress.TryParse(host, out var address) && IPAddress.IsLoopback(address);
    }
}
