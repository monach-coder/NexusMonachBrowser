using System.Text.Json;

namespace NexusMonach.Services;

public enum ExtensionRiskVerdict { Safe, Caution, Dangerous }

/// <summary>
/// Отчёт риска расширения: счёт, вердикт и человекочитаемые причины
/// для диалога подтверждения при импорте.
/// </summary>
public sealed record ExtensionRiskReport(
    int Score,
    ExtensionRiskVerdict Verdict,
    List<string> Reasons)
{
    public string Summary => Verdict switch
    {
        ExtensionRiskVerdict.Dangerous =>
            "Опасная комбинация прав. Установку должен подтвердить сам пользователь.",
        ExtensionRiskVerdict.Caution =>
            "Широкие права — расширение многое может, проверьте, что это ваше.",
        _ => "Права умеренные."
    };
}

/// <summary>
/// Страж расширений: риск-скоринг manifest.json при импорте. Чистая
/// функция над разобранным манифестом — проверяется юнит-тестами.
/// Браузер — привратник: опасные комбинации прав по умолчанию
/// блокируются с объяснением, установка возможна только осознанно.
/// </summary>
public static class ExtensionRiskAnalyzer
{
    private static readonly (string Permission, int Weight, string Why)[] Weights =
    {
        ("webRequestBlocking", 4, "Перехват и изменение всех запросов"),
        ("webRequest", 2, "Наблюдение за сетевыми запросами"),
        ("nativeMessaging", 4, "Мост к программам вне браузера"),
        ("debugger", 6, "Полный отладочный доступ к страницам"),
        ("proxy", 3, "Подмена прокси и маршрута трафика"),
        ("cookies", 2, "Чтение cookies всех сайтов"),
        ("history", 2, "Чтение истории посещений"),
        ("management", 2, "Управление другими расширениями"),
        ("downloads", 1, "Доступ к загрузкам"),
        ("tabs", 1, "Доступ к содержимому вкладок"),
        ("clipboardWrite", 1, "Запись в буфер обмена"),
    };

    public static ExtensionRiskReport Analyze(JsonElement root)
    {
        var reasons = new List<string>();
        var score = 0;

        // Права из permissions и optional_permissions.
        foreach (var section in new[] { "permissions", "optional_permissions" })
        {
            if (!root.TryGetProperty(section, out var node) ||
                node.ValueKind != JsonValueKind.Array) continue;
            foreach (var item in node.EnumerateArray())
            {
                var permission = item.GetString();
                if (string.IsNullOrEmpty(permission)) continue;
                var match = Weights.FirstOrDefault(w =>
                    w.Permission.Equals(permission, StringComparison.Ordinal));
                if (match.Permission is null) continue;
                // Опциональные права чуть дешевле обязательных.
                var weight = section == "permissions" ? match.Weight : (match.Weight + 1) / 2;
                score += weight;
                reasons.Add($"{permission}: {match.Why}");
            }
        }

        // Широкие хост-права: один шаблон на все сайты.
        if (CoversAllSites(root, "host_permissions") ||
            CoversAllSites(root, "permissions"))
        {
            score += 4;
            reasons.Add("host_permissions: доступ ко всем сайтам без исключений");
        }

        // Контент-скрипты на все сайты — исполнение своего кода везде.
        if (root.TryGetProperty("content_scripts", out var scripts) &&
            scripts.ValueKind == JsonValueKind.Array)
        {
            foreach (var script in scripts.EnumerateArray())
            {
                if (!script.TryGetProperty("matches", out var matches) ||
                    matches.ValueKind != JsonValueKind.Array) continue;
                if (matches.EnumerateArray().Any(m =>
                        m.GetString() is "<all_urls>" or "*://*/*"))
                {
                    score += 3;
                    reasons.Add("content_scripts: свой код исполняется на всех сайтах");
                    break;
                }
            }
        }

        var verdict = score >= 7 ? ExtensionRiskVerdict.Dangerous
            : score >= 3 ? ExtensionRiskVerdict.Caution
            : ExtensionRiskVerdict.Safe;
        return new ExtensionRiskReport(score, verdict,
            reasons.Distinct().ToList());
    }

    private static bool CoversAllSites(JsonElement root, string section)
    {
        if (!root.TryGetProperty(section, out var node) ||
            node.ValueKind != JsonValueKind.Array) return false;
        return node.EnumerateArray().Any(item =>
            item.GetString() is "<all_urls>" or "*://*/*" or "http://*/*" or "https://*/*");
    }
}
