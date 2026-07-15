using System.Globalization;
using System.Net;

namespace NexusMonach.Services;

public enum PhishingRiskLevel
{
    None,
    Medium,
    High
}

public sealed record PhishingAssessment(PhishingRiskLevel Level, string Description, string Host);

public static class PhishingProtectionService
{
    private static readonly HashSet<string> SessionTrustedHosts = new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string[]> OfficialBrands = new(StringComparer.OrdinalIgnoreCase)
    {
        ["google"] = ["google.com", "google.ru", "google.co.uk", "google.de", "google.fr", "googleusercontent.com"],
        ["microsoft"] = ["microsoft.com", "microsoftonline.com", "live.com", "office.com"],
        ["github"] = ["github.com", "githubusercontent.com"],
        ["paypal"] = ["paypal.com"],
        ["apple"] = ["apple.com", "icloud.com"],
        ["amazon"] = ["amazon.com"],
        ["yandex"] = ["yandex.ru", "ya.ru"],
        ["gosuslugi"] = ["gosuslugi.ru"],
        ["sberbank"] = ["sberbank.ru", "sber.ru"],
        ["tbank"] = ["tbank.ru", "tinkoff.ru"]
    };

    public static PhishingAssessment Analyze(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || UrlService.IsInternal(url))
            return new PhishingAssessment(PhishingRiskLevel.None, string.Empty, string.Empty);

        var asciiHost = uri.IdnHost.ToLowerInvariant();
        if (SessionTrustedHosts.Contains(asciiHost))
            return new PhishingAssessment(PhishingRiskLevel.None, string.Empty, asciiHost);

        string unicodeHost;
        try { unicodeHost = new IdnMapping().GetUnicode(asciiHost); }
        catch { unicodeHost = asciiHost; }

        var hasLatin = unicodeHost.Any(IsLatin);
        var hasCyrillic = unicodeHost.Any(IsCyrillic);
        if (hasLatin && hasCyrillic)
            return new PhishingAssessment(PhishingRiskLevel.High,
                "В домене смешаны латинские и кириллические буквы — это характерный приём подмены адреса", asciiHost);

        var normalized = NormalizeConfusables(unicodeHost);
        foreach (var (brand, officialDomains) in OfficialBrands)
        {
            if (officialDomains.Any(x => IsOfficialHost(asciiHost, x)))
                continue;

            var labels = normalized.Split('.', StringSplitOptions.RemoveEmptyEntries);
            if (labels.Any(label => LevenshteinDistance(label, brand) == 1))
                return new PhishingAssessment(PhishingRiskLevel.Medium,
                    $"Домен очень похож на {brand}, но не принадлежит официальному адресу", asciiHost);

            var suspiciousWords = normalized.Contains("login", StringComparison.OrdinalIgnoreCase) ||
                                  normalized.Contains("secure", StringComparison.OrdinalIgnoreCase) ||
                                  normalized.Contains("verify", StringComparison.OrdinalIgnoreCase) ||
                                  normalized.Contains("account", StringComparison.OrdinalIgnoreCase);
            if (suspiciousWords && normalized.Contains(brand, StringComparison.OrdinalIgnoreCase))
                return new PhishingAssessment(PhishingRiskLevel.High,
                    $"Название {brand} совмещено с призывом войти или подтвердить учётную запись", asciiHost);
        }

        if (IPAddress.TryParse(uri.Host.Trim('[', ']'), out _))
            return new PhishingAssessment(PhishingRiskLevel.Medium,
                "Сайт открыт по IP-адресу вместо обычного доменного имени", asciiHost);

        if (asciiHost.Contains("xn--", StringComparison.OrdinalIgnoreCase))
            return new PhishingAssessment(PhishingRiskLevel.Medium,
                "Адрес содержит международное доменное имя Punycode — внимательно проверьте его написание", asciiHost);

        if (uri.Scheme != Uri.UriSchemeHttps)
            return new PhishingAssessment(PhishingRiskLevel.Medium,
                "Соединение не защищено HTTPS", asciiHost);

        if (!uri.IsDefaultPort)
            return new PhishingAssessment(PhishingRiskLevel.Medium,
                "Сайт использует нестандартный сетевой порт", asciiHost);

        return new PhishingAssessment(PhishingRiskLevel.None, string.Empty, asciiHost);
    }

    public static void TrustForSession(string host)
    {
        if (!string.IsNullOrWhiteSpace(host)) SessionTrustedHosts.Add(host);
    }

    private static bool IsOfficialHost(string actual, string official) =>
        actual.Equals(official, StringComparison.OrdinalIgnoreCase) ||
        actual.EndsWith('.' + official, StringComparison.OrdinalIgnoreCase);

    private static bool IsLatin(char c) => c is >= '\u0041' and <= '\u007A' or >= '\u00C0' and <= '\u024F';
    private static bool IsCyrillic(char c) => c is >= '\u0400' and <= '\u052F';

    private static string NormalizeConfusables(string value)
    {
        var map = new Dictionary<char, char>
        {
            ['а'] = 'a', ['е'] = 'e', ['о'] = 'o', ['р'] = 'p', ['с'] = 'c', ['х'] = 'x',
            ['у'] = 'y', ['і'] = 'i', ['к'] = 'k', ['м'] = 'm', ['т'] = 't', ['в'] = 'b', ['н'] = 'h'
        };
        return new string(value.ToLowerInvariant().Select(c => map.TryGetValue(c, out var replacement) ? replacement : c).ToArray());
    }

    private static int LevenshteinDistance(string left, string right)
    {
        if (Math.Abs(left.Length - right.Length) > 1) return 2;
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1];
            current[0] = i;
            for (var j = 1; j <= right.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
            previous = current;
        }
        return previous[right.Length];
    }
}
