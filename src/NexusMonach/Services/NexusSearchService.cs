using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using NexusMonach.Models;

namespace NexusMonach.Services;

public sealed record NexusSearchItem(string Title, string Url, string Snippet, string Answer, double Score);
public sealed record NexusSearchReport(string Query, string DirectAnswer, IReadOnlyList<NexusSearchItem> Items,
    string Disclosure);

/// <summary>
/// Bounded research crawler used by the local Nexus results page. A configured
/// search provider is queried only for discovery; page cleaning, ranking,
/// summarisation and click feedback remain on the device.
/// </summary>
public static partial class NexusSearchService
{
    private const int MaximumResults = 5;
    private const int MaximumPageBytes = 900_000;
    private static readonly SemaphoreSlim FeedbackGate = new(1, 1);
    private static string FeedbackFile => Path.Combine(AppPaths.AppRoot, "search-feedback.json");

    public static async Task<NexusSearchReport> SearchAsync(string query,
        IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        query = Regex.Replace(query ?? string.Empty, @"\s+", " ").Trim();
        if (query.Length < 2) throw new ArgumentException("Введите поисковый запрос.", nameof(query));
        if (query.Length > 300) query = query[..300];

        progress?.Report("Получаю кандидатов у выбранной поисковой системы…");
        using var client = CreateClient();
        var discovered = await DiscoverWithFallbackAsync(client, BuildDiscoveryQuery(query), progress, cancellationToken);
        if (discovered.Count == 0)
            throw new InvalidOperationException("Поисковая система не вернула читаемых ссылок. Попробуйте другой запрос или провайдера в настройках.");

        progress?.Report($"Crawl Engine читает {Math.Min(MaximumResults, discovered.Count)} наиболее подходящих страниц (не более двух одновременно)…");
        var candidates = discovered.Take(MaximumResults).ToArray();
        using var crawlerSlots = new SemaphoreSlim(2, 2);
        async Task<NexusSearchCandidate?> EnrichBoundedAsync(NexusSearchCandidate item)
        {
            await crawlerSlots.WaitAsync(cancellationToken);
            try { return await EnrichAsync(client, item, cancellationToken); }
            finally { crawlerSlots.Release(); }
        }
        var fetchTasks = candidates.Select(EnrichBoundedAsync).ToArray();
        var enriched = (await Task.WhenAll(fetchTasks)).Where(x => x is not null).Cast<NexusSearchCandidate>().ToList();
        if (enriched.Count == 0) enriched.AddRange(candidates.Take(MaximumResults));

        var feedback = await ReadFeedbackAsync();
        foreach (var item in enriched)
            item.LocalPreference = feedback.TryGetValue(PreferenceKey(query, item.Url), out var count)
                ? Math.Min(2.0, Math.Log2(count + 1) * .35)
                : 0;

        if (AiModelCatalog.SemanticReady)
        {
            using var semanticBudget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            semanticBudget.CancelAfter(TimeSpan.FromSeconds(32));
            try
            {
                progress?.Report("Nexus Semantics ранжирует страницы по смыслу запроса…");
                var queryVector = await NexusFabricRuntime.EmbedSemanticsAsync("query: " + query, semanticBudget.Token);
                foreach (var item in enriched)
                {
                    var text = "passage: " + item.Title + " " + item.Snippet[..Math.Min(item.Snippet.Length, 2400)];
                    var vector = await NexusFabricRuntime.EmbedSemanticsAsync(text, semanticBudget.Token);
                    item.SemanticRelevance = Math.Max(0, Cosine(queryVector, vector));
                }
                enriched = enriched.OrderByDescending(x => x.SemanticRelevance + x.LocalPreference * .1).ToList();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { progress?.Report("Смысловое ранжирование заняло слишком долго — использую локальную быструю оценку…"); }
        }

        progress?.Report("Локальная модель сопоставляет факты и готовит выжимку…");
        return await LocalIntelligenceService.AnalyzeWebSearchAsync(query, enriched, cancellationToken);
    }

    public static async Task<NexusSearchReport> AnalyzeSelectedSiteAsync(string query, string title, string url,
        string currentPageText, IReadOnlyList<string> internalLinks, IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        query = Regex.Replace(query ?? string.Empty, @"\s+", " ").Trim();
        progress?.Report("Открытая страница очищена от навигации и рекламы.");
        var candidates = new List<NexusSearchCandidate>
        {
            new()
            {
                Title = string.IsNullOrWhiteSpace(title) ? url : title,
                Url = url,
                Snippet = (currentPageText ?? string.Empty)[..Math.Min(currentPageText?.Length ?? 0, 12_000)],
                SemanticRelevance = 1
            }
        };
        using var client = CreateClient();
        using var slots = new SemaphoreSlim(2, 2);
        var safeLinks = internalLinks.Where(link => IsSafePublicUrl(link, out var uri) &&
                                                    Uri.TryCreate(url, UriKind.Absolute, out var root) &&
                                                    IsSameSite(uri.Host, root.Host))
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(6)
            .Select(link => new NexusSearchCandidate { Title = link, Url = link, Snippet = string.Empty })
            .ToArray();
        async Task<NexusSearchCandidate?> ReadAsync(NexusSearchCandidate candidate)
        {
            await slots.WaitAsync(cancellationToken);
            try
            {
                var result = await EnrichAsync(client, candidate, cancellationToken);
                if (!string.IsNullOrWhiteSpace(result?.Snippet))
                    progress?.Report("Прочитан ещё один релевантный раздел сайта.");
                return result;
            }
            finally { slots.Release(); }
        }
        progress?.Report($"Проверяю релевантные разделы: {safeLinks.Length}.");
        var enriched = await Task.WhenAll(safeLinks.Select(ReadAsync));
        candidates.AddRange(enriched.Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Snippet))
            .Select(x => x!));
        progress?.Report($"Сопоставляю факты из материалов: {candidates.Count}.");
        return await LocalIntelligenceService.AnalyzeWebSearchAsync(query, candidates, cancellationToken);
    }

    private static string BuildDiscoveryQuery(string query)
    {
        var cleaned = Regex.Replace(query,
            @"^(?:пожалуйста\s+)?(?:открой|открыть|покажи|показать|найди|найти|ищу|search|find|show|open)\s+",
            string.Empty, RegexOptions.IgnoreCase).Trim();
        // Translate only explicit search intent, never page content. This makes a
        // Russian natural-language request for foreign-language sources useful to
        // providers that otherwise treat every Russian word as a required token.
        if (Regex.IsMatch(cleaned, @"новостн\p{L}*\s+сайт", RegexOptions.IgnoreCase) &&
            Regex.IsMatch(cleaned, @"английск\p{L}*\s+язык", RegexOptions.IgnoreCase))
            cleaned = "English language news websites";
        return cleaned.Length >= 2 ? cleaned : query;
    }

    public static async Task RecordChoiceAsync(string query, string url)
    {
        if (string.IsNullOrWhiteSpace(query) || !IsSafePublicUrl(url, out _)) return;
        await FeedbackGate.WaitAsync();
        try
        {
            var values = await ReadFeedbackAsync();
            var key = PreferenceKey(query, url);
            values[key] = values.TryGetValue(key, out var count) ? Math.Min(100, count + 1) : 1;
            await JsonStore.WriteAsync(FeedbackFile, values);
            await KnowledgeGraphService.RecordResearchChoiceAsync(query, url);
        }
        finally { FeedbackGate.Release(); }
    }

    public static bool IsAllowedResultUrl(string url) => IsSafePublicUrl(url, out _);

    private static HttpClient CreateClient()
    {
        var settings = SettingsService.Current;
        HttpMessageHandler handler;
        if (settings.EnableCustomProxy && ProxyConfigurationService.TryValidate(settings.ProxyHost, settings.ProxyPort, out _))
        {
            var scheme = settings.ProxyKind == ProxyKind.Socks5 ? "socks5" : "http";
            handler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false,
                UseCookies = false,
                Proxy = new WebProxy($"{scheme}://{settings.ProxyHost}:{settings.ProxyPort}"),
                UseProxy = true
            };
        }
        else
        {
            handler = new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.All,
                AllowAutoRedirect = false,
                UseCookies = false,
                UseProxy = false,
                ConnectTimeout = TimeSpan.FromSeconds(8),
                PooledConnectionLifetime = TimeSpan.FromMinutes(2),
                ConnectCallback = NexusSearchNetworkGuard.ConnectPublicAsync
            };
        }
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(16) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/136 Safari/537.36 NexusMonach/2.7");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.6");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return client;
    }

    private static async Task<List<NexusSearchCandidate>> DiscoverWithFallbackAsync(HttpClient client, string query,
        IProgress<string>? progress, CancellationToken cancellationToken)
    {
        var configured = SettingsService.Current.SearchEngine;
        SearchEngineKind[] fallbackOrder = [configured, SearchEngineKind.DuckDuckGo,
            SearchEngineKind.Bing, SearchEngineKind.Mojeek];
        Exception? lastError = null;
        foreach (var provider in fallbackOrder.Distinct())
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            budget.CancelAfter(TimeSpan.FromSeconds(11));
            try
            {
                progress?.Report($"Получаю стартовые ссылки: {ProviderName(provider)}…");
                var result = await DiscoverAsync(client, query, provider, budget.Token);
                if (result.Count > 0) return result;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            { lastError = new TimeoutException($"{ProviderName(provider)} не ответил вовремя."); }
            catch (HttpRequestException ex) { lastError = ex; }
        }
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException(
            "Ни одна доступная поисковая система не вернула читаемые стартовые ссылки. " +
            "Проверьте соединение или выберите другую систему в настройках.", lastError);
    }

    private static async Task<List<NexusSearchCandidate>> DiscoverAsync(HttpClient client, string query,
        SearchEngineKind provider, CancellationToken cancellationToken)
    {
        var providerUrl = BuildProviderUrl(provider, query);
        await NexusSearchNetworkGuard.ValidatePublicDestinationAsync(providerUrl, cancellationToken);
        using var providerResponse = await client.GetAsync(providerUrl, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        providerResponse.EnsureSuccessStatusCode();
        var html = await ReadBoundedHtmlAsync(providerResponse, cancellationToken);
        var providerHost = new Uri(providerUrl).Host;
        var result = new List<NexusSearchCandidate>();
        foreach (Match match in AnchorRegex().Matches(html))
        {
            var raw = WebUtility.HtmlDecode(match.Groups["url"].Value);
            var title = CleanText(match.Groups["title"].Value, 180);
            var url = UnwrapResultUrl(raw);
            if (title.Length < 3 || !IsSafePublicUrl(url, out var uri) ||
                uri.Host.Equals(providerHost, StringComparison.OrdinalIgnoreCase) ||
                IsSearchProviderHost(uri.Host) || result.Any(x => x.Url.Equals(uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase)))
                continue;
            result.Add(new NexusSearchCandidate { Title = title, Url = uri.AbsoluteUri, Snippet = title });
            if (result.Count >= 12) break;
        }
        return result;
    }

    private static async Task<NexusSearchCandidate?> EnrichAsync(HttpClient client, NexusSearchCandidate candidate,
        CancellationToken cancellationToken)
    {
        try
        {
            await NexusSearchNetworkGuard.ValidatePublicDestinationAsync(candidate.Url, cancellationToken);
            using var response = await client.GetAsync(candidate.Url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType is not string media ||
                !media.Contains("html", StringComparison.OrdinalIgnoreCase)) return candidate;
            var html = await ReadBoundedHtmlAsync(response, cancellationToken);
            candidate.Snippet = ExtractReadableText(html, 12_000);
            return candidate;
        }
        catch (OperationCanceledException) { throw; }
        catch { return candidate; }
    }

    private static async Task<string> ReadBoundedHtmlAsync(HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > MaximumPageBytes)
            throw new HttpRequestException("Crawl Engine отклонил страницу, превышающую лимит ответа.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var bytes = new byte[16_384];
        while (buffer.Length < MaximumPageBytes)
        {
            var remaining = (int)Math.Min(bytes.Length, MaximumPageBytes - buffer.Length);
            var read = await stream.ReadAsync(bytes.AsMemory(0, remaining), cancellationToken);
            if (read == 0) break;
            buffer.Write(bytes, 0, read);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }

    private static string BuildProviderUrl(SearchEngineKind provider, string query)
    {
        var escaped = Uri.EscapeDataString(query);
        return provider switch
        {
            SearchEngineKind.Brave => $"https://search.brave.com/search?q={escaped}&source=web",
            SearchEngineKind.Startpage => $"https://www.startpage.com/sp/search?query={escaped}",
            SearchEngineKind.Google => $"https://www.google.com/search?q={escaped}&num=10",
            SearchEngineKind.Yandex => $"https://yandex.ru/search/?text={escaped}",
            SearchEngineKind.Bing => $"https://www.bing.com/search?q={escaped}&count=10",
            SearchEngineKind.Mojeek => $"https://www.mojeek.com/search?q={escaped}",
            _ => $"https://html.duckduckgo.com/html/?q={escaped}"
        };
    }

    private static string ProviderName(SearchEngineKind provider) => provider switch
    {
        SearchEngineKind.Brave => "Brave Search",
        SearchEngineKind.Startpage => "Startpage",
        SearchEngineKind.Google => "Google",
        SearchEngineKind.Yandex => "Яндекс",
        SearchEngineKind.Bing => "Bing",
        SearchEngineKind.Mojeek => "Mojeek",
        _ => "DuckDuckGo"
    };

    private static string UnwrapResultUrl(string value)
    {
        if (value.StartsWith("//", StringComparison.Ordinal)) value = "https:" + value;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return string.Empty;
        if (uri.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(uri.Query, @"(?:^|[?&])uddg=([^&]+)", RegexOptions.IgnoreCase);
            if (match.Success) return Uri.UnescapeDataString(match.Groups[1].Value.Replace('+', ' '));
        }
        if (uri.Host.Contains("google.", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath == "/url")
        {
            var match = Regex.Match(uri.Query, @"(?:^|[?&])q=([^&]+)", RegexOptions.IgnoreCase);
            if (match.Success) return Uri.UnescapeDataString(match.Groups[1].Value.Replace('+', ' '));
        }
        if (uri.Host.Contains("bing.", StringComparison.OrdinalIgnoreCase) && uri.AbsolutePath.StartsWith("/ck/", StringComparison.OrdinalIgnoreCase))
        {
            var match = Regex.Match(uri.Query, @"(?:^|[?&])u=([^&]+)", RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var encoded = Uri.UnescapeDataString(match.Groups[1].Value);
                if (encoded.StartsWith("a1", StringComparison.Ordinal)) encoded = encoded[2..];
                encoded = encoded.Replace('-', '+').Replace('_', '/');
                encoded = encoded.PadRight(encoded.Length + (4 - encoded.Length % 4) % 4, '=');
                try
                {
                    var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
                    if (Uri.TryCreate(decoded, UriKind.Absolute, out var target)) return target.AbsoluteUri;
                }
                catch (FormatException) { }
            }
        }
        return uri.AbsoluteUri;
    }

    private static bool IsSafePublicUrl(string value, out Uri uri)
        => NexusSearchNetworkGuard.TryParsePublicHttpUri(value, out uri);

    private static bool IsSearchProviderHost(string host) =>
        host.Contains("duckduckgo.", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("startpage.", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("google.", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("yandex.", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("bing.", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("mojeek.", StringComparison.OrdinalIgnoreCase) ||
        host.Contains("brave.", StringComparison.OrdinalIgnoreCase);

    private static bool IsSameSite(string left, string right) =>
        left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
        left.EndsWith('.' + right, StringComparison.OrdinalIgnoreCase) ||
        right.EndsWith('.' + left, StringComparison.OrdinalIgnoreCase);

    private static string ExtractReadableText(string html, int maximum)
    {
        html = ScriptStyleRegex().Replace(html, " ");
        html = TagRegex().Replace(html, " ");
        return CleanText(html, maximum);
    }

    private static string CleanText(string value, int maximum)
    {
        value = WebUtility.HtmlDecode(TagRegex().Replace(value, " "));
        value = Regex.Replace(value, @"\s+", " ").Trim();
        return value[..Math.Min(value.Length, maximum)];
    }

    private static async Task<Dictionary<string, int>> ReadFeedbackAsync() =>
        await JsonStore.ReadAsync<Dictionary<string, int>>(FeedbackFile) ?? new Dictionary<string, int>(StringComparer.Ordinal);

    private static string PreferenceKey(string query, string url)
    {
        var topic = string.Join('-', Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}").Select(x => x.Value).Take(6));
        var host = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : url;
        return topic + "|" + host;
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count) return 0;
        double dot = 0, leftLength = 0, rightLength = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftLength += left[index] * left[index];
            rightLength += right[index] * right[index];
        }
        return leftLength <= 0 || rightLength <= 0 ? 0 : dot / Math.Sqrt(leftLength * rightLength);
    }

    [GeneratedRegex("<a\\b[^>]*?href\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>(?<title>[\\s\\S]*?)</a>", RegexOptions.IgnoreCase)]
    private static partial Regex AnchorRegex();
    [GeneratedRegex("<(script|style|noscript|svg|nav|footer|form)\\b[\\s\\S]*?</\\1>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();
    [GeneratedRegex("<[^>]+>")]
    private static partial Regex TagRegex();
}

public sealed class NexusSearchCandidate
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public double LocalPreference { get; set; }
    public double SemanticRelevance { get; set; }
}
