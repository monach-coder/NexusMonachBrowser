using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace Nexus.Guardian;

internal enum HealthStatus { Ok, Warn, Fail }

internal sealed record HealthCheckItem(string Id, HealthStatus Status, string Detail);

internal enum HealthVerdict { Ok, Warn, Fail }

internal sealed class StartupHealthReport
{
    public HealthVerdict Verdict { get; init; }
    /// <summary>Краткая строка для env браузеру: «ok», «warn:disk,webview2», «fail:integrity».</summary>
    public string Compact { get; init; } = "ok";
    public IReadOnlyList<HealthCheckItem> Checks { get; init; } = [];
}

/// <summary>
/// Стартовая самодиагностика Guardian: локальные проверки механизмов до
/// запуска браузера (целостность, WebView2, место на диске, права записи,
/// состояние отложенного обновления). Результат — отчёт в Guardian\Reports\
/// и краткий статус в NEXUS_STARTUP_HEALTH браузеру. Проверки мгновенны и
/// никогда не бросают: отчёт важнее, старт не задерживается.
/// </summary>
internal static class StartupHealthCheck
{
    private const int KeepReports = 20;
    private const long FailFreeBytes = 500L * 1024 * 1024;
    private const long WarnFreeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>Чистое решение по результатам проверок (для тестов).</summary>
    internal static HealthVerdict Decide(IReadOnlyList<HealthCheckItem> items) =>
        items.Any(x => x.Status == HealthStatus.Fail) ? HealthVerdict.Fail :
        items.Any(x => x.Status == HealthStatus.Warn) ? HealthVerdict.Warn : HealthVerdict.Ok;

    internal static string BuildCompact(HealthVerdict verdict, IReadOnlyList<HealthCheckItem> items)
    {
        if (verdict == HealthVerdict.Ok) return "ok";
        var bad = string.Join(",", items.Where(x => x.Status != HealthStatus.Ok).Select(x => x.Id));
        return (verdict == HealthVerdict.Fail ? "fail:" : "warn:") + bad;
    }

    public static StartupHealthReport Run(string applicationRoot, string guardianRoot,
        IntegrityResult integrity)
    {
        var items = new List<HealthCheckItem>();

        items.Add(new("integrity",
            integrity.CanLaunch ? HealthStatus.Ok : HealthStatus.Fail,
            integrity.CompactStatus +
            (integrity.Problems.Count > 0 ? ": " + string.Join("; ", integrity.Problems.Take(3)) : "")));

        var webview = DetectWebView2RuntimeVersion();
        items.Add(new("webview2",
            webview is null ? HealthStatus.Fail : HealthStatus.Ok,
            webview is null ? "WebView2 Runtime не найден — браузер не откроет вкладки" : "runtime " + webview));

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(applicationRoot))!);
            if (!drive.IsReady) items.Add(new("disk", HealthStatus.Fail, "диск не готов"));
            else if (drive.AvailableFreeSpace < FailFreeBytes)
                items.Add(new("disk", HealthStatus.Fail,
                    $"свободно {drive.AvailableFreeSpace / 1024.0 / 1024.0:F0} МБ — обновление не влезет"));
            else if (drive.AvailableFreeSpace < WarnFreeBytes)
                items.Add(new("disk", HealthStatus.Warn,
                    $"свободно {drive.AvailableFreeSpace / 1024.0 / 1024.0:F0} МБ"));
            else
                items.Add(new("disk", HealthStatus.Ok,
                    $"свободно {drive.AvailableFreeSpace / 1024.0 / 1024.0 / 1024.0:F1} ГБ"));
        }
        catch (Exception ex) { items.Add(new("disk", HealthStatus.Warn, ex.Message)); }

        items.Add(CheckWriteAccess(applicationRoot));
        items.Add(CheckPendingUpdate(applicationRoot, guardianRoot));
        items.Add(CheckRecentApplyError(guardianRoot));

        var verdict = Decide(items);
        var report = new StartupHealthReport
        {
            Verdict = verdict,
            Compact = BuildCompact(verdict, items),
            Checks = items
        };
        WriteReport(applicationRoot, guardianRoot, report);
        return report;
    }

    private static HealthCheckItem CheckWriteAccess(string applicationRoot)
    {
        try
        {
            var probe = Path.Combine(applicationRoot, ".nexus-write-probe-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            // Прецедент 04.09: UnauthorizedAccessException при применении
            // обновления. Если лаунчер не может писать в каталог установки,
            // апликатор тоже не сможет — предупреждаем заранее.
            return new HealthCheckItem("write-access", HealthStatus.Ok, "запись в каталог установки доступна");
        }
        catch (Exception ex)
        {
            return new HealthCheckItem("write-access", HealthStatus.Fail,
                "нет прав записи в каталог установки: " + ex.Message);
        }
    }

    private static HealthCheckItem CheckPendingUpdate(string applicationRoot, string guardianRoot)
    {
        try
        {
            if (SilentUpdateCoordinator.IsPendingReady(applicationRoot, guardianRoot))
                return new HealthCheckItem("update-pending", HealthStatus.Ok,
                    "накопленное обновление применится при старте");
            var pendingPath = SilentUpdateCoordinator.PendingPath(guardianRoot);
            if (File.Exists(pendingPath))
                return new HealthCheckItem("update-pending", HealthStatus.Warn,
                    "pending-update.json есть, но неприменим (стейджинг битый?)");
            var rejected = Path.Combine(Path.GetDirectoryName(pendingPath)!, "pending-update.rejected.json");
            if (File.Exists(rejected) && File.GetLastWriteTimeUtc(rejected) > DateTime.UtcNow.AddDays(-1))
                return new HealthCheckItem("update-pending", HealthStatus.Warn,
                    "обновление отклонено после повторных неудач — скачай и поставь вручную");
            return new HealthCheckItem("update-pending", HealthStatus.Ok, "нет накопленного обновления");
        }
        catch (Exception ex)
        {
            return new HealthCheckItem("update-pending", HealthStatus.Warn, ex.Message);
        }
    }

    private static HealthCheckItem CheckRecentApplyError(string guardianRoot)
    {
        try
        {
            var path = Path.Combine(guardianRoot, "Updates", "apply-error.log");
            if (!File.Exists(path)) return new HealthCheckItem("update-apply", HealthStatus.Ok, "ошибок применения нет");
            if (File.GetLastWriteTimeUtc(path) < DateTime.UtcNow.AddDays(-1))
                return new HealthCheckItem("update-apply", HealthStatus.Ok, "последняя ошибка применения старше суток");
            var last = File.ReadLines(path).LastOrDefault(line => line.Length > 0);
            return new HealthCheckItem("update-apply", HealthStatus.Warn,
                "недавняя ошибка применения: " + (last ?? "?"));
        }
        catch (Exception ex)
        {
            return new HealthCheckItem("update-apply", HealthStatus.Warn, ex.Message);
        }
    }

    private static string? DetectWebView2RuntimeVersion()
    {
        const string subkey =
            @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            try
            {
                using var key = RegistryKey.OpenBaseKey(hive, RegistryView.Registry32).OpenSubKey(subkey);
                if (key?.GetValue("pv") is string version &&
                    !string.IsNullOrWhiteSpace(version) && version != "0.0.0.0")
                    return version;
            }
            catch { /* реестр недоступен — пробуем каталог */ }
        }

        try
        {
            var webviewRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                "Microsoft", "EdgeWebView", "Application");
            if (Directory.Exists(webviewRoot))
            {
                var latest = Directory.GetDirectories(webviewRoot)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name) && char.IsDigit(name![0]))
                    .OrderByDescending(name => name, StringComparer.OrdinalIgnoreCase)
                    .FirstOrDefault();
                if (latest is not null) return latest;
            }
        }
        catch { /* каталог недоступен */ }
        return null;
    }

    private static void WriteReport(string applicationRoot, string guardianRoot,
        StartupHealthReport report)
    {
        try
        {
            var reportsRoot = Path.Combine(guardianRoot, "Reports");
            Directory.CreateDirectory(reportsRoot);
            var id = Guid.NewGuid().ToString("N")[..12];
            var path = Path.Combine(reportsRoot,
                $"startup-health-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{id}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                schema = 1,
                utc = DateTimeOffset.UtcNow,
                browserVersion = FileVersionInfo.GetVersionInfo(
                    Path.Combine(applicationRoot, "NexusMonach.Browser.exe"))
                    .ProductVersion ?? "unknown",
                launcherVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                osVersion = Environment.OSVersion.VersionString,
                processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                verdict = report.Verdict.ToString().ToLowerInvariant(),
                compact = report.Compact,
                checks = report.Checks.Select(x => new
                {
                    id = x.Id, status = x.Status.ToString().ToLowerInvariant(), detail = x.Detail
                })
            }, new JsonSerializerOptions { WriteIndented = true }));

            // Ротация: храним последние KeepReports отчётов.
            var stale = Directory.GetFiles(reportsRoot, "startup-health-*.json")
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .Skip(KeepReports);
            foreach (var old in stale) try { File.Delete(old); } catch { }
        }
        catch { /* отчёт не должен ломать запуск */ }
    }
}
