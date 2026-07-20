using System.Text;
using System.Text.Json;

namespace NexusMonach.Services;

/// <summary>
/// Неперсональный локальный журнал Следопыта. Он намеренно не принимает запросы,
/// URL, DOM, названия товаров и тексты ошибок: только технические этапы и счётчики.
/// </summary>
public static class SledopytDiagnosticsService
{
    private const int MaximumEntries = 160;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static int Count
    {
        get { lock (Gate) return ReadUnsafe().Count; }
    }

    public static void Record(string operation, string stage, string outcome,
        long durationMilliseconds = 0, int candidateCount = 0, int resultCount = 0,
        string code = "ok")
    {
        var entry = new SledopytDiagnosticEntry
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            Operation = SafeToken(operation),
            Stage = SafeToken(stage),
            Outcome = SafeToken(outcome),
            Code = SafeToken(code),
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
            try
            {
                var directory = Path.GetDirectoryName(AppPaths.SledopytDiagnosticsFile)!;
                Directory.CreateDirectory(directory);
                var temporary = AppPaths.SledopytDiagnosticsFile + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(entries, JsonOptions),
                    new UTF8Encoding(false));
                File.Move(temporary, AppPaths.SledopytDiagnosticsFile, true);
            }
            catch { /* Диагностика никогда не должна мешать браузеру. */ }
        }
    }

    public static string FormatForDisplay()
    {
        lock (Gate)
        {
            var entries = ReadUnsafe();
            if (entries.Count == 0)
                return "Следопыт ещё не выполнял исследование или поиск товаров.\n\n" + PrivacyNotice;
            var lines = new List<string>
            {
                "ЛОКАЛЬНЫЙ ЖУРНАЛ NEXUS СЛЕДОПЫТ",
                PrivacyNotice,
                string.Empty
            };
            lines.AddRange(entries.OrderByDescending(x => x.TimestampUtc).Select(x =>
                $"{x.TimestampUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss} · {Describe(x.Operation)} · " +
                $"{Describe(x.Stage)} · {Describe(x.Outcome)} · {x.DurationMilliseconds} мс · " +
                $"кандидатов {x.CandidateCount} · результатов {x.ResultCount} · код {x.Code}"));
            return string.Join(Environment.NewLine, lines);
        }
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
        "started" => "запуск",
        "page-read" => "страница прочитана",
        "links-read" => "разделы собраны",
        "completed" => "завершено",
        "cancelled" => "отменено",
        "failed" => "ошибка",
        "success" => "успешно",
        "partial" => "частично",
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
    public long DurationMilliseconds { get; set; }
    public int CandidateCount { get; set; }
    public int ResultCount { get; set; }
}
