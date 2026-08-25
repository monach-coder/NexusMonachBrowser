using System.Text;
using System.Text.Json;

namespace NexusMonach.Services;

/// <summary>
/// Неперсональный локальный журнал Следопыта. Он намеренно не принимает запросы,
/// URL, DOM, названия товаров и тексты ошибок: только технические этапы и счётчики.
/// </summary>
public static class SledopytDiagnosticsService
{
    private const int MaximumEntries = 400;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Count
    {
        get { lock (Gate) return ReadUnsafe().Count; }
    }

    public static string Begin(string operation, string trigger, string surface)
    {
        var runId = Guid.NewGuid().ToString("N")[..12];
        Record(operation, "requested", "success", code: "accepted",
            runId: runId, trigger: trigger, surface: surface);
        return runId;
    }

    public static void Record(string operation, string stage, string outcome,
        long durationMilliseconds = 0, int candidateCount = 0, int resultCount = 0,
        string code = "ok", string runId = "legacy", string trigger = "unknown",
        string surface = "unknown")
    {
        var entry = new SledopytDiagnosticEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Operation = SafeToken(operation),
            Stage = SafeToken(stage),
            Outcome = SafeToken(outcome),
            Code = SafeToken(code),
            RunId = SafeToken(runId),
            Trigger = SafeToken(trigger),
            Surface = SafeToken(surface),
            DurationMilliseconds = Math.Clamp(durationMilliseconds, 0, 60 * 60 * 1000),
            CandidateCount = Math.Clamp(candidateCount, 0, 100_000),
            ResultCount = Math.Clamp(resultCount, 0, 10_000)
        };
        lock (Gate)
        {
            var entries = ReadUnsafe();
            entries.Add(entry);
            if (entries.Count > MaximumEntries)
                entries.RemoveRange(0, entries.Count - MaximumEntries);
            try { WriteUnsafe(entries); }
            catch { /* Диагностика никогда не должна мешать браузеру. */ }
        }
    }

    private static void WriteUnsafe(IReadOnlyList<SledopytDiagnosticEntry> entries)
    {
        var destination = AppPaths.SledopytDiagnosticsFile;
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory,
            $".{Path.GetFileName(destination)}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions),
                new UTF8Encoding(false));
            File.Move(temporary, destination, true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
        }
    }

    public static string FormatForDisplay()
    {
        lock (Gate)
        {
            return FormatForDisplay(ReadUnsafe());
        }
    }

    internal static string FormatForDisplay(IReadOnlyList<SledopytDiagnosticEntry> entries)
    {
        var lines = new List<string>
        {
            "ПОЛНЫЙ ЛОКАЛЬНЫЙ РАПОРТ NEXUS СЛЕДОПЫТ",
            PrivacyNotice,
            string.Empty,
            "КОГДА ОН СТАРТУЕТ",
            "• Сравнение товаров: после кнопки Следопыта нужно ввести запрос и нажать «Начать поиск» или Enter.",
            "• На магазине сначала используется поиск самого сайта; если карточки не читаются, включается ограниченный поиск по этому домену.",
            "• На новой вкладке и странице поисковой системы выполняется общий поиск через настроенную поисковую машину.",
            "• Фоновое исследование сайта запускается после поискового запроса, когда результат открыт в той же вкладке.",
            string.Empty
        };
        if (entries.Count == 0)
        {
            lines.Add("Попыток запуска пока не зарегистрировано.");
            return string.Join(Environment.NewLine, lines);
        }

        var runs = GroupRuns(entries)
            .OrderByDescending(group => group[0].TimestampUtc)
            .ToArray();
        var completed = runs.Count(run => run.Any(x => x.Stage == "completed" && x.Outcome == "success"));
        var blocked = runs.Count(run => run.Any(x => x.Stage == "blocked"));
        var failed = runs.Count(run => run.Any(x => x.Stage == "failed"));
        var cancelled = runs.Count(run => run.Any(x => x.Stage == "cancelled"));
        var unfinished = runs.Length - completed - blocked - failed - cancelled;
        lines.Add("СВОДКА");
        lines.Add($"Попыток: {runs.Length} · успешно: {completed} · заблокировано до старта: {blocked} · " +
                  $"ошибок: {failed} · отменено: {cancelled} · без финала: {Math.Max(0, unfinished)}");
        lines.Add(string.Empty);

        foreach (var run in runs)
        {
            var first = run[0];
            lines.Add($"ЗАПУСК {first.TimestampUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss} · #{first.RunId} · " +
                      $"{Describe(first.Operation)} · {Describe(first.Trigger)} · {Describe(first.Surface)}");
            foreach (var entry in run)
                lines.Add($"  {entry.TimestampUtc.ToLocalTime():HH:mm:ss.fff} · {Describe(entry.Stage)} · " +
                          $"{Describe(entry.Outcome)} · {entry.DurationMilliseconds} мс · " +
                          $"кандидатов {entry.CandidateCount} · результатов {entry.ResultCount} · " +
                          $"код {Describe(entry.Code)}");
            lines.Add(string.Empty);
        }
        return string.Join(Environment.NewLine, lines);
    }

    private static IReadOnlyList<SledopytDiagnosticEntry[]> GroupRuns(
        IReadOnlyList<SledopytDiagnosticEntry> entries)
    {
        var ordered = entries.OrderBy(x => x.TimestampUtc).ToArray();
        var result = new List<SledopytDiagnosticEntry[]>();
        foreach (var explicitRun in ordered
                     .Where(x => !string.IsNullOrWhiteSpace(x.RunId) && x.RunId != "legacy")
                     .GroupBy(x => x.RunId, StringComparer.Ordinal))
            result.Add(explicitRun.OrderBy(x => x.TimestampUtc).ToArray());

        // Older journal versions had no run identifier. Reconstruct their
        // started -> terminal sequences so one historical attempt is not shown
        // as several unrelated attempts after an upgrade.
        var active = new Dictionary<string, List<SledopytDiagnosticEntry>>(StringComparer.Ordinal);
        foreach (var entry in ordered.Where(x => string.IsNullOrWhiteSpace(x.RunId) || x.RunId == "legacy"))
        {
            active.TryGetValue(entry.Operation, out var run);
            if (entry.Stage is "started" or "requested" || run is null)
            {
                if (run is { Count: > 0 }) result.Add(run.ToArray());
                run = [];
                active[entry.Operation] = run;
            }
            run.Add(entry);
            if (entry.Stage is "completed" or "blocked" or "failed" or "cancelled")
            {
                result.Add(run.ToArray());
                active.Remove(entry.Operation);
            }
        }
        result.AddRange(active.Values.Where(x => x.Count > 0).Select(x => x.ToArray()));
        return result;
    }

    public static bool Export(string destinationPath)
    {
        if (string.IsNullOrWhiteSpace(destinationPath)) return false;
        try
        {
            List<SledopytDiagnosticEntry> entries;
            lock (Gate) entries = ReadUnsafe();
            var payload = new
            {
                schemaVersion = 2,
                generatedAtUtc = DateTimeOffset.UtcNow,
                privacy = PrivacyNotice,
                entries
            };
            File.WriteAllText(destinationPath, JsonSerializer.Serialize(payload, JsonOptions),
                new UTF8Encoding(false));
            return true;
        }
        catch { return false; }
    }

    private const string PrivacyNotice =
        "Хранится только на этом компьютере. Запросы, адреса страниц, DOM и личные данные не записываются.";

    private static List<SledopytDiagnosticEntry> ReadUnsafe()
    {
        try
        {
            if (!File.Exists(AppPaths.SledopytDiagnosticsFile)) return [];
            return JsonSerializer.Deserialize<List<SledopytDiagnosticEntry>>(
                       File.ReadAllText(AppPaths.SledopytDiagnosticsFile)) ?? [];
        }
        catch { return []; }
    }

    private static string SafeToken(string value)
    {
        value = new string((value ?? string.Empty).Where(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_').ToArray());
        return string.IsNullOrWhiteSpace(value) ? "unknown" : value[..Math.Min(value.Length, 40)];
    }

    private static string Describe(string token) => token switch
    {
        "site-research" => "исследование сайта",
        "shopping" => "поиск товаров",
        "requested" => "запрос принят",
        "blocked" => "не запущено",
        "preflight" => "проверка условий",
        "panel-opened" => "панель открыта",
        "search-submit" => "поиск отправлен",
        "page-extracted" => "карточки страницы собраны",
        "provider-search" => "поиск через поисковую машину",
        "vision" => "локальное распознавание изображения",
        "site-fallback" => "резервный поиск по домену",
        "ranking" => "локальное сравнение",
        "started" => "запуск",
        "page-read" => "страница прочитана",
        "links-read" => "разделы собраны",
        "completed" => "завершено",
        "cancelled" => "отменено",
        "failed" => "ошибка",
        "toolbar" => "кнопка панели браузера",
        "button-or-enter" => "кнопка «Начать поиск» / Enter",
        "omnibox" => "поисковый запрос в адресной строке",
        "nexus-search" => "локальная страница Nexus Search",
        "site" => "обычный сайт",
        "search-provider" => "страница поисковой системы",
        "new-tab" => "новая вкладка",
        "missing-query" => "не введён запрос",
        "tab-unavailable" => "активная вкладка недоступна",
        "model-unavailable" => "локальная модель недоступна",
        "waiting-result" => "ожидание открытия результата в этой вкладке",
        "superseded-query" => "заменено новым поисковым запросом",
        "page-changed-before-start" => "страница изменилась до чтения",
        "navigation-or-timeout" => "переход на другую страницу или лимит времени",
        "user-or-timeout" => "остановлено пользователем или по лимиту времени",
        "network" => "сетевая ошибка",
        "timeout" => "истёк лимит времени",
        "stage-timeout" => "сетевой этап превысил собственный лимит времени",
        "vision-unavailable" => "локальный комплект Nexus Vision неполный",
        "vision-failed" => "локальное распознавание изображения не завершено",
        "invalid-response" => "ответ не удалось разобрать",
        "catalog-unavailable" => "каталог не дал проверяемого сравнения",
        "operation-error" => "непредвиденная ошибка операции",
        "search-field-not-found" => "поиск сайта не найден или выдача не изменилась",
        "site-search-confirmed" => "сайт подтвердил обновление выдачи",
        "no-cards" => "карточки товаров не извлечены",
        "accepted" => "условия запуска проверяются",
        "success" => "успешно",
        "partial" => "частично",
        "fallback" => "резервный путь",
        _ => token
    };
}

public sealed class SledopytDiagnosticEntry
{
    public DateTimeOffset TimestampUtc { get; set; }
    public string Operation { get; set; } = "unknown";
    public string Stage { get; set; } = "unknown";
    public string Outcome { get; set; } = "unknown";
    public string Code { get; set; } = "ok";
    public string RunId { get; set; } = "legacy";
    public string Trigger { get; set; } = "unknown";
    public string Surface { get; set; } = "unknown";
    public long DurationMilliseconds { get; set; }
    public int CandidateCount { get; set; }
    public int ResultCount { get; set; }
}
