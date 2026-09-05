using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace NexusMonach.Services;

/// <summary>
/// Стартовый самотест механизмов браузера: через несколько секунд после
/// запуска собирает их статусы (маршрутизатор сети, WebView2, порт-щит,
/// голос) и дописывает в последний отчёт Guardian startup-health-*.json —
/// картина «что проверил лаунчер до старта + что реально живо в браузере»
/// оказывается в одном файле. Проблемы озвучиваются голосом, полный отчёт —
/// в Центре Guardian. Никогда не бросает и не блокирует браузер.
/// </summary>
public static class StartupSelfTestService
{
    private sealed record BrowserCheck(string Id, string Status, string Detail);

    private static string ReportsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusMonach", "Guardian", "Reports");

    public static void Start()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Механизмы поднимаются не мгновенно: даём им вздохнуть,
                // потом спрашиваем статус.
                await Task.Delay(TimeSpan.FromSeconds(12));
                var checks = CollectChecks();
                AppendToGuardianReport(checks);
                AnnounceProblems(checks);
            }
            catch { /* самотест не имеет права мешать браузеру */ }
        });
    }

    private static List<BrowserCheck> CollectChecks()
    {
        var checks = new List<BrowserCheck>();

        var routerRunning = Chain.ChainRouterService.IsRunning;
        checks.Add(new BrowserCheck("chain-router",
            routerRunning ? "ok" : "fail",
            routerRunning ? "маршрутизатор сети работает" : "маршрутизатор сети не поднят"));

        try
        {
            var webview = WebView2RuntimeMonitor.Check();
            var state = webview.State.ToString().ToLowerInvariant();
            var status = state.Contains("missing") || state.Contains("fail") ? "fail"
                : state.Contains("restart") ? "warn"
                : "ok";
            checks.Add(new BrowserCheck("webview2", status,
                $"{state}: {webview.Message}"));
        }
        catch (Exception ex)
        {
            checks.Add(new BrowserCheck("webview2", "warn", ex.Message));
        }

        try
        {
            var mode = SettingsService.Current.PortShieldMode;
            var active = PortShieldService.IsSessionShieldActive;
            checks.Add(new BrowserCheck("port-shield",
                mode == PortShieldMode.Off || active ? "ok" : "warn",
                mode == PortShieldMode.Off ? "выключен в настройках"
                    : active ? "правила применены"
                    : "правила не применены"));
        }
        catch (Exception ex)
        {
            checks.Add(new BrowserCheck("port-shield", "warn", ex.Message));
        }

        try
        {
            var engine = VoiceAssistantService.EngineStatus;
            checks.Add(new BrowserCheck("voice",
                string.IsNullOrWhiteSpace(engine) ? "warn" : "ok",
                string.IsNullOrWhiteSpace(engine) ? "статус движка неизвестен" : "движок: " + engine));
        }
        catch (Exception ex)
        {
            checks.Add(new BrowserCheck("voice", "warn", ex.Message));
        }

        return checks;
    }

    /// <summary>Дописывает браузерные проверки в самый свежий отчёт Guardian.</summary>
    private static void AppendToGuardianReport(IReadOnlyList<BrowserCheck> checks)
    {
        try
        {
            var root = ReportsRoot;
            if (!Directory.Exists(root)) return;
            var latest = Directory.GetFiles(root, "startup-health-*.json")
                .OrderByDescending(x => x, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (latest is null) return;

            var node = JsonNode.Parse(File.ReadAllText(latest));
            if (node is null) return;
            var array = new JsonArray();
            foreach (var check in checks)
                array.Add(new JsonObject
                {
                    ["id"] = check.Id,
                    ["status"] = check.Status,
                    ["detail"] = check.Detail
                });
            node["browserCheckedUtc"] = DateTimeOffset.UtcNow;
            node["browserChecks"] = array;
            File.WriteAllText(latest, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* отчёт желателен, но не обязательный */ }
    }

    private static void AnnounceProblems(IReadOnlyList<BrowserCheck> checks)
    {
        try
        {
            var failed = checks.Where(x => x.Status == "fail").ToList();
            var warned = checks.Where(x => x.Status == "warn").ToList();

            if (failed.Count > 0)
            {
                CrashReportService.AddBreadcrumb("startup-health",
                    "fail: " + string.Join(",", failed.Select(x => x.Id)));
                VoiceAssistantService.Announce(
                    "Внимание: не работает " +
                    string.Join(", ", failed.Select(x => NameOf(x.Id))) +
                    ". Отчёт сохранён, подробности в Центре Стража.",
                    VoiceAnnouncementPriority.Important);
            }
            else if (warned.Count > 0)
            {
                CrashReportService.AddBreadcrumb("startup-health",
                    "warn: " + string.Join(",", warned.Select(x => x.Id)));
                VoiceAssistantService.Announce(
                    "Замечание при старте: " +
                    string.Join(", ", warned.Select(x => NameOf(x.Id))) + ".",
                    VoiceAnnouncementPriority.Progress);
            }
        }
        catch { /* голос желателен, но не обязательный */ }
    }

    private static string NameOf(string id) => id switch
    {
        "chain-router" => "маршрутизатор сети",
        "webview2" => "ядро WebView2",
        "port-shield" => "порт-щит",
        "voice" => "голос",
        _ => id
    };
}
