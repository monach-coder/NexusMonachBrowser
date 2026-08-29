using System.Net.Http;
using System.Text.RegularExpressions;

namespace NexusMonach.Services;

/// <summary>
/// Тихий результат Следопыта: лучший кандидат по запросу и его отрыв
/// от страницы, где пользователь находится сейчас.
/// </summary>
public sealed record SledopytSilentResult(
    string Query,
    string BestUrl,
    string BestTitle,
    double BestScore,
    string CurrentUrl,
    double CurrentScore,
    double Gap,
    DateTimeOffset ReadyUtc);

/// <summary>
/// Тихий Следопыт: наблюдает за поиском пользователя и помогает, не мешая.
/// Как только запрос введён — начинает искать в фоне: кандидаты читаются
/// из открытой вкладки выдачи (только чтение DOM, никаких навигаций и
/// прокруток), топ-ссылки подтягиваются HttpClient'ом мимо вкладок.
/// Релевантность — терминное совпадение + граф знаний (частота визитов
/// хоста) + обучение на выборах пользователя. Оповещение — один тихий
/// голосовой сигнал «нашёл лучше», окно Следопыта открывается только
/// кнопкой. Знакомый запрос (пользователь явно знает, куда идёт) —
/// порог оповещения выше.
/// </summary>
public static partial class SledopytQuietEye
{
    public static SledopytSilentResult? PendingResult { get; private set; }
    public static event Action? ResultReady;

    private static int _running;
    private static SledopytRelevance.LearningModel _learning = null!;
    private static string LearningPath => Path.Combine(AppPaths.AppRoot, "sledopyt-learning.json");

    private static SledopytRelevance.LearningModel Learning =>
        _learning ??= SledopytRelevance.LearningModel.Load(LearningPath);

    /// <summary>
    /// Начинает тихий поиск по запросу. Кандидаты снимаются с вкладки
    /// выдачи только чтением; сети — HttpClient, вкладки пользователя
    /// не трогаются вообще.
    /// </summary>
    public static void ObserveQuery(Models.BrowserTab serpTab, string query, bool isPrivate)
    {
        if (isPrivate || string.IsNullOrWhiteSpace(query)) return;
        if (Interlocked.Exchange(ref _running, 1) != 0) return;
        _ = Task.Run(() => RunAsync(serpTab, query));
    }

    /// <summary>
    /// Обучение: пользователь открыл конкретный результат по запросу —
    /// это лучший сигнал релевантности, какой только бывает.
    /// </summary>
    public static void RecordChoice(string query, string url)
    {
        if (string.IsNullOrWhiteSpace(query) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return;
        Learning.Record(query, uri.Host.ToLowerInvariant());
        Learning.Save(LearningPath);
        CrashReportService.AddBreadcrumb("sledopyt", "choice-recorded");
    }

    private static async Task RunAsync(Models.BrowserTab serpTab, string query)
    {
        try
        {
            // Выдаче нужно пару секунд на прогрузку; пользователь пока
            // свободно листает — мы ничего не перехватываем.
            await Task.Delay(TimeSpan.FromSeconds(2));
            var links = await serpTab.GetResearchLinksAsync(query, 12);
            if (links.Count == 0) return;

            var visits = HostVisitCounts();
            var learning = Learning;

            // Первый проход: терминные совпадения по адресу.
            var candidates = links
                .Select(url => new { Url = url, Score = SledopytRelevance.BaseScore(query, "", url) })
                .Where(c => c.Score > 0 || SledopytRelevance.FinalScore(0, c.Url, query, visits, learning) > 1)
                .OrderByDescending(c => c.Score + SledopytRelevance.FinalScore(0, c.Url, query, visits, learning) * 0.3)
                .Take(4)
                .ToList();
            if (candidates.Count == 0) return;

            // Второй проход: тихий фетч топ-кандидатов мимо вкладок.
            var fetched = new List<(string Url, string Title, double Score)>();
            using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(9) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd(
                    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0 Safari/537.36");
                foreach (var candidate in candidates)
                {
                    try
                    {
                        using var response = await http.GetAsync(candidate.Url);
                        if (!response.IsSuccessStatusCode) continue;
                        var html = await response.Content.ReadAsStringAsync();
                        var title = ExtractTitle(html);
                        var baseScore = SledopytRelevance.BaseScore(query, title, candidate.Url);
                        var final = SledopytRelevance.FinalScore(baseScore, candidate.Url, query, visits, learning);
                        fetched.Add((candidate.Url, title, final));
                    }
                    catch { /* недоступный кандидат — просто пропускаем */ }
                }
            }
            if (fetched.Count == 0) return;
            var best = fetched.OrderByDescending(c => c.Score).First();

            // Где сейчас пользователь: если ушёл с выдачи — сравниваем
            // с его текущей страницей; не ушёл — ждём, результат дозреет.
            var currentUrl = serpTab.CurrentUrl;
            if (UrlService.IsSearchProviderUrl(currentUrl) || UrlService.IsInternal(currentUrl))
            {
                // Пользователь ещё выбирает: тихий результат сохраняем,
                // оповещение не звучит — не мешаем листать выдачу.
                PendingResult = new SledopytSilentResult(query, best.Url, best.Title,
                    best.Score, currentUrl, 0, 0, DateTimeOffset.Now);
                ResultReady?.Invoke();
                CrashReportService.AddBreadcrumb("sledopyt", "silent-ready-on-serp");
                return;
            }

            var currentTitle = serpTab.Title;
            var currentBase = SledopytRelevance.BaseScore(query, currentTitle, currentUrl);
            var currentScore = SledopytRelevance.FinalScore(currentBase, currentUrl, query, visits, learning);
            var gap = best.Score - currentScore;
            PendingResult = new SledopytSilentResult(query, best.Url, best.Title,
                best.Score, currentUrl, currentScore, gap, DateTimeOffset.Now);
            ResultReady?.Invoke();

            // Знакомый запрос — пользователь знает путь: молчим, порог выше.
            var familiar = SledopytRelevance.IsFamiliar(query, currentUrl, visits, learning);
            var threshold = familiar
                ? SledopytRelevance.AnnounceGapFamiliar
                : SledopytRelevance.AnnounceGapDefault;
            var alreadyThere = best.Url.Equals(currentUrl, StringComparison.OrdinalIgnoreCase);
            if (gap >= threshold && !alreadyThere)
            {
                Ui.Post(() => VoiceAssistantService.Announce(
                    "Следопыт нашёл, возможно, лучший ответ по вашему запросу. Открыть — кнопка Следопыта.",
                    VoiceAnnouncementPriority.Important));
                CrashReportService.AddBreadcrumb("sledopyt", "silent-announce-gap-" + Math.Round(gap, 1));
            }
            else
            {
                CrashReportService.AddBreadcrumb("sledopyt",
                    familiar ? "silent-user-knows" : "silent-no-gap");
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("sledopyt", "silent-observer", ex);
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    /// <summary>Частоты визитов хостов из графа знаний.</summary>
    internal static Dictionary<string, int> HostVisitCounts()
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var node in KnowledgeGraphService.Snapshot().Nodes)
            {
                if (string.IsNullOrEmpty(node.Domain)) continue;
                counts[node.Domain.ToLowerInvariant()] =
                    counts.GetValueOrDefault(node.Domain.ToLowerInvariant()) + node.VisitCount;
            }
        }
        catch { /* граф недоступен — скоринг без приоритета */ }
        return counts;
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    private static string ExtractTitle(string html)
    {
        var match = TitleRegex().Match(html);
        if (!match.Success) return string.Empty;
        var title = System.Net.WebUtility.HtmlDecode(match.Groups[1].Value);
        return Regex.Replace(title, @"\s+", " ").Trim() is { Length: > 0 } clean ? clean[..Math.Min(160, clean.Length)] : string.Empty;
    }
}
