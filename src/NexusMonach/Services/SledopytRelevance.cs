using System.Text.Json;

namespace NexusMonach.Services;

/// <summary>
/// Кандидат для тихого поиска Следопыта: ссылка со страницы выдачи и её
/// вычисленная релевантность.
/// </summary>
public sealed record SledopytCandidate(
    string Url,
    string Title,
    double BaseScore,
    double FinalScore);

/// <summary>
/// Релевантность и обучение Следопыта — чистое ядро тихого поиска.
/// Скоринг: совпадение терминов запроса с заголовком и адресом,
/// плюс «знание пользователя» (граф знаний: частота визитов хоста),
/// плюс выученные веса (какие хосты пользователь реально выбирал
/// по похожим запросам раньше). Знакомый запрос — тише оповещение.
/// </summary>
public static class SledopytRelevance
{
    /// <summary>Порог преимущества, при котором стоит тихо оповестить.</summary>
    public const double AnnounceGapDefault = 2.0;
    /// <summary>Знакомый запрос (сильный приор из графа/обучения) — порог выше,
    /// чтобы не дёргать пользователя, который и так знает, куда идёт.</summary>
    public const double AnnounceGapFamiliar = 4.0;

    // ── Скоринг ───────────────────────────────────────────────────

    /// <summary>Разбирает запрос на нормализованные термины.</summary>
    public static string[] Terms(string query) =>
        (query ?? string.Empty)
        .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(t => t.Trim("«»,.;:!?()[]".ToCharArray()).ToLowerInvariant())
        .Where(t => t.Length > 1)
        .Distinct()
        .ToArray();

    /// <summary>
    /// Базовый скор ссылки: заголовок весит втрое больше адреса.
    /// Чистая функция — проверяется юнит-тестами.
    /// </summary>
    public static double BaseScore(string query, string title, string url)
    {
        var terms = Terms(query);
        if (terms.Length == 0) return 0;
        var titleLower = (title ?? string.Empty).ToLowerInvariant();
        var urlLower = (url ?? string.Empty).ToLowerInvariant();
        var titleHits = terms.Count(t => titleLower.Contains(t, StringComparison.Ordinal));
        var urlHits = terms.Count(t => urlLower.Contains(t, StringComparison.Ordinal));
        var titleScore = 3.0 * titleHits / terms.Length;
        var urlScore = 1.0 * urlHits / terms.Length;
        // Полное покрытие заголовка — сильный сигнал.
        if (titleHits == terms.Length) titleScore += 1.5;
        return titleScore + urlScore;
    }

    /// <summary>
    /// Итоговый скор с учётом знания пользователя: частота визитов хоста
    /// из графа знаний и выученный вес «по этому запросу выбирали этот хост».
    /// </summary>
    public static double FinalScore(double baseScore, string url, string query,
        IReadOnlyDictionary<string, int> hostVisitCounts, LearningModel learning)
    {
        var host = TryHost(url);
        var visits = host is not null && hostVisitCounts.TryGetValue(host, out var count) ? count : 0;
        var graphBoost = Math.Min(2.0, Math.Log2(visits + 1) * 0.5);
        var learned = host is not null ? learning.Weight(query, host) : 0;
        return baseScore + graphBoost + learned;
    }

    /// <summary>
    /// Достаточно ли уверенный пользователь (граф + обучение), чтобы
    /// поднять порог оповещения: он явно знает, куда идёт.
    /// </summary>
    public static bool IsFamiliar(string query, string url,
        IReadOnlyDictionary<string, int> hostVisitCounts, LearningModel learning)
    {
        var host = TryHost(url);
        if (host is null) return false;
        var visits = hostVisitCounts.TryGetValue(host, out var count) ? count : 0;
        return visits >= 3 || learning.Weight(query, host) >= 1.0;
    }

    private static string? TryHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;

    // ── Обучение ──────────────────────────────────────────────────

    /// <summary>
    /// Выученные веса: «стем запроса → хост → сколько раз выбрали».
    /// Персист в Data/sledopyt-learning.json.
    /// </summary>
    public sealed class LearningModel
    {
        public Dictionary<string, Dictionary<string, double>> Weights { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Стем запроса: первые два термина — устойчивый ключ похожих запросов.</summary>
        public static string Stem(string query)
        {
            var terms = Terms(query);
            return terms.Length == 0 ? string.Empty : string.Join(' ', terms.Take(2));
        }

        public double Weight(string query, string host)
        {
            var stem = Stem(query);
            return stem.Length > 0 && Weights.TryGetValue(stem, out var hosts) &&
                   hosts.TryGetValue(host, out var weight)
                ? weight : 0;
        }

        public void Record(string query, string host, double delta = 0.5)
        {
            var stem = Stem(query);
            if (stem.Length == 0 || string.IsNullOrEmpty(host)) return;
            if (!Weights.TryGetValue(stem, out var hosts))
            {
                hosts = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                Weights[stem] = hosts;
            }
            hosts[host] = Math.Min(4.0, (hosts.TryGetValue(host, out var current) ? current : 0) + delta);
        }

        public static LearningModel Load(string path)
        {
            try
            {
                if (File.Exists(path))
                    return JsonSerializer.Deserialize<LearningModel>(File.ReadAllText(path)) ?? new LearningModel();
            }
            catch { /* повреждённый файл обучения не должен ломать поиск */ }
            return new LearningModel();
        }

        public void Save(string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { /* обучение — не критичные данные */ }
        }
    }
}
