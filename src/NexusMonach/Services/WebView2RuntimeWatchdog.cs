using System.Net.Http;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace NexusMonach.Services;

/// <summary>
/// Сторож движка: сверяет установленную версию Evergreen WebView2 с
/// актуальной стабильной в каталоге Microsoft. Окно уязвимостей до патчей
/// не устранить, но сузить с недель до часов — можно: голосом просим
/// обновить рантайм, если он отстаёт. Проверка раз в сессию, офлайн —
/// тихий пропуск.
/// </summary>
public static class WebView2RuntimeWatchdog
{
    private const string CatalogUrl = "https://edgeupdates.microsoft.com/api/products";

    /// <summary>
    /// Сравнивает версии и предупреждает голосом, если установленный
    /// рантайм отстаёт от стабильного канала больше, чем на допуск.
    /// </summary>
    public static async Task CheckAsync()
    {
        try
        {
            var installedRaw = CoreWebView2Environment.GetAvailableBrowserVersionString();
            if (string.IsNullOrWhiteSpace(installedRaw)) return;
            var installed = ParseVersion(installedRaw);
            if (installed is null) return;

            var latest = await FetchLatestStableAsync();
            if (latest is null) return;

            // Допуск: малые расхождения не тревожим — важны мажор и билд.
            var outdated = latest.Major > installed.Major ||
                           (latest.Major == installed.Major && latest.Build > installed.Build + 2);
            if (!outdated) return;

            CrashReportService.AddBreadcrumb("runtime-watchdog",
                $"outdated: {installedRaw} < {latest}");
            Ui.Post(() => VoiceAssistantService.Announce(
                $"Движок браузера устарел: установлена версия {installed}, актуальная {latest}. " +
                "Обновите компонент Edge WebView2 — сузим окно известных уязвимостей.",
                VoiceAnnouncementPriority.Important));
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("runtime-watchdog", "check", ex);
        }
    }

    /// <summary>Разбирает строку версии в мажор.минор.билд.ревизия.</summary>
    internal static Version? ParseVersion(string raw)
    {
        var groups = raw.Split('.').Select(part =>
                new string(part.TakeWhile(char.IsDigit).ToArray()))
            .Where(part => part.Length > 0)
            .Take(4).Select(int.Parse).ToList();
        return groups.Count == 4 ? new Version(groups[0], groups[1], groups[2], groups[3]) : null;
    }

    /// <summary>Тянет стабильный канал для Windows из каталога Evergreen.</summary>
    internal static async Task<Version?> FetchLatestStableAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        var bytes = await http.GetByteArrayAsync(CatalogUrl);
        using var doc = JsonDocument.Parse(bytes);
        foreach (var product in doc.RootElement.EnumerateArray())
        {
            if (!product.TryGetProperty("Product", out var name) ||
                !string.Equals(name.GetString(), "Stable", StringComparison.OrdinalIgnoreCase))
                continue;
            foreach (var release in product.GetProperty("Releases").EnumerateArray())
            {
                if (!release.TryGetProperty("Platform", out var platform) ||
                    !string.Equals(platform.GetString(), "Windows 64-bit", StringComparison.OrdinalIgnoreCase))
                    continue;
                if (release.TryGetProperty("ProductVersion", out var version) &&
                    ParseVersion(version.GetString() ?? "") is { } parsed)
                    return parsed;
            }
        }
        return null;
    }
}
