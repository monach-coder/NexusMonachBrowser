using System.Security.Cryptography;

namespace NexusMonach.Services;

/// <summary>
/// Tamper-evidence профиля: при выходе снимается хеш-манифест
/// пользовательских файлов, при следующем старте сверяется. Изменение
/// между сессиями = кто-то трогал профиль, пока браузер был закрыт.
/// Сверяем только файлы, меняющиеся внутри сессии (их захватывает
/// выходной снимок): session.json пишется при старте — исключён.
/// </summary>
public static class ProfileIntegrityService
{
    private static readonly string[] GuardedFiles =
    [
        "settings.json",
        "bookmarks.json",
        "extensions.json",
        "knowledge-graph.json"
    ];

    private static string ManifestPath => Path.Combine(AppPaths.AppRoot, "Guardian", "profile-integrity.json");

    /// <summary>Снимок хешей guarded-файлов на момент вызова.</summary>
    internal static Dictionary<string, string> CaptureSnapshot()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in GuardedFiles)
        {
            var path = Path.Combine(AppPaths.AppRoot, name);
            if (!File.Exists(path)) continue;
            using var stream = File.OpenRead(path);
            snapshot[name] = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        return snapshot;
    }

    /// <summary>Фиксирует снимок при выходе. Вызывается из OnExit.</summary>
    public static void CaptureAtExit()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ManifestPath)!);
            var snapshot = CaptureSnapshot();
            File.WriteAllText(ManifestPath,
                System.Text.Json.JsonSerializer.Serialize(snapshot));
            CrashReportService.AddBreadcrumb("profile-integrity", "captured-" + snapshot.Count);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("profile-integrity", "capture", ex);
        }
    }

    /// <summary>
    /// Сверяет профиль при старте. Изменение файлов, пока браузер был
    /// закрыт, — голосовое предупреждение и след в breadcrumbs.
    /// </summary>
    public static void VerifyAtStartup()
    {
        try
        {
            if (!File.Exists(ManifestPath)) return;
            var saved = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(
                File.ReadAllText(ManifestPath));
            if (saved is null || saved.Count == 0) return;

            var current = CaptureSnapshot();
            var changed = saved
                .Where(entry => current.TryGetValue(entry.Key, out var hash) && hash != entry.Value)
                .Select(entry => entry.Key)
                .ToList();
            if (changed.Count > 0)
            {
                CrashReportService.AddBreadcrumb("profile-integrity",
                    "tampered:" + string.Join(",", changed));
                Ui.Post(() => VoiceAssistantService.Announce(
                    "Внимание: файлы профиля изменились, пока браузер был закрыт: " +
                    string.Join(", ", changed) + ". Если это были не вы — проверьте машину.",
                    VoiceAnnouncementPriority.Critical));
            }
            else
            {
                CrashReportService.AddBreadcrumb("profile-integrity", "verified");
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("profile-integrity", "verify", ex);
        }
    }
}
