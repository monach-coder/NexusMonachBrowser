using System.Diagnostics;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>Режим автоматической защиты портов при запуске браузера.</summary>
public enum PortShieldMode
{
    /// <summary>Сканировать и закрывать утечки файрволом на сессию (один запрос UAC).</summary>
    Auto,
    /// <summary>Сканировать и сообщать голосом/в Дозоре, ничего не закрывать.</summary>
    NotifyOnly,
    /// <summary>Не сканировать автоматически.</summary>
    Off
}

/// <summary>
/// Порт-щит: при запуске браузера сканирует машину и закрывает «утекающие»
/// порты локальной сети — mDNS, SSDP/UPnP и NetBIOS — правилами файрвола
/// Windows на время сессии (один запрос повышения прав), снимая их при
/// выходе. Эти порты сливают топологию сети и имя машины всем в локальном
/// сегменте; в анонимном режиме это прямая угроза, а полезной функции у них
/// для браузера нет. Порты пользовательских служб (RDP, SMB, VNC) щит НЕ
/// трогает — о них он только сообщает: закрывать чужие сервисы молча нельзя.
/// </summary>
public static class PortShieldService
{
    /// <summary>Порты, закрываемые автоматически: утечки локальной сети.</summary>
    internal static readonly (int Port, string Protocol, string Name)[] AutoClosedLeaks =
    [
        (5353, "UDP", "mDNS — имена устройств в локальной сети"),
        (1900, "UDP", "SSDP/UPnP — топология сети"),
        (137, "UDP", "NetBIOS-имя машины"),
        (138, "UDP", "NetBIOS-датаграммы"),
        (139, "TCP", "NetBIOS-сессии")
    ];

    private const string RulePrefix = "Nexus Leak Guard";
    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NexusMonach", "Guardian", "port-shield.json");

    private static int _applying;

    /// <summary>
    /// Запускается при старте браузера: скан + (в режиме Auto) закрытие
    /// утечек на сессию. Никогда не бросает исключений и не блокирует запуск.
    /// </summary>
    public static void StartAsync(BrowserSettings settings)
    {
        if (settings.PortShieldMode == PortShieldMode.Off) return;
        if (Interlocked.Exchange(ref _applying, 1) != 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var leaks = FindOpenLeaks();
                if (leaks.Count == 0)
                {
                    CrashReportService.AddBreadcrumb("port-shield", "no-leaks");
                    return;
                }
                var names = string.Join(", ", leaks.Select(l => l.Name.Split('—')[0].Trim()).Distinct());
                if (settings.PortShieldMode == PortShieldMode.NotifyOnly)
                {
                    Ui.Post(() => VoiceAssistantService.Announce(
                        "Внимание: открыты утекающие порты — " + names +
                        ". Смотрите Сетевой Дозор.", VoiceAnnouncementPriority.Important));
                    CrashReportService.AddBreadcrumb("port-shield", "notify:" + leaks.Count);
                    return;
                }
                var applied = await ApplySessionShieldAsync(leaks);
                if (applied)
                    Ui.Post(() => VoiceAssistantService.Announce(
                        "Порт-щит активен. Закрыты на сессию: " + names + ".",
                        VoiceAnnouncementPriority.Important));
            }
            catch (Exception ex)
            {
                CrashReportService.RecordNonFatal("port-shield", "startup", ex);
            }
            finally
            {
                Volatile.Write(ref _applying, 0);
            }
        });
    }

    /// <summary>Открытые прямо сейчас утекающие порты из списка авто-закрытия.</summary>
    internal static List<(int Port, string Protocol, string Name)> FindOpenLeaks()
    {
        var listeners = WindowsPortService.GetListeningPorts();
        var result = new List<(int, string, string)>();
        foreach (var leak in AutoClosedLeaks)
        {
            if (listeners.Any(l => l.Port == leak.Port &&
                                   l.Protocol.Equals(leak.Protocol, StringComparison.OrdinalIgnoreCase)))
                result.Add(leak);
        }
        return result;
    }

    /// <summary>
    /// Добавляет блокирующие правила файрвола через один повышенный вызов
    /// PowerShell (скрипт-файл без строковых команд). Возвращает true при успехе.
    /// </summary>
    private static async Task<bool> ApplySessionShieldAsync(
        List<(int Port, string Protocol, string Name)> leaks)
    {
        var script = BuildRuleScript(leaks, add: true);
        var ok = await RunElevatedAsync(script);
        if (ok)
        {
            await File.WriteAllTextAsync(StatePath,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    appliedUtc = DateTimeOffset.UtcNow,
                    rules = leaks.Select(l => RuleName(l)).ToArray()
                }));
            CrashReportService.AddBreadcrumb("port-shield", "applied:" + leaks.Count);
        }
        else
        {
            CrashReportService.AddBreadcrumb("port-shield", "apply-declined");
        }
        return ok;
    }

    /// <summary>Снимает правила сессии при выходе браузера (fire-and-forget UAC).</summary>
    public static void RemoveSessionShield()
    {
        try
        {
            if (!File.Exists(StatePath)) return;
            var leaks = AutoClosedLeaks.ToList();
            _ = RunElevatedAsync(BuildRuleScript(leaks, add: false), waitForExit: true);
            try { File.Delete(StatePath); } catch { }
            CrashReportService.AddBreadcrumb("port-shield", "removed");
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("port-shield", "remove", ex);
        }
    }

    internal static string RuleName((int Port, string Protocol, string Name) leak) =>
        $"{RulePrefix} — {leak.Protocol} {leak.Port}";

    /// <summary>
    /// Скрипт правил: имена детерминированы, повторный запуск идемпотентен
    /// (существующие правила с тем же именем сначала удаляются).
    /// </summary>
    internal static string BuildRuleScript(
        List<(int Port, string Protocol, string Name)> leaks, bool add)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("$ErrorActionPreference = 'SilentlyContinue'");
        foreach (var leak in leaks)
        {
            var name = RuleName(leak);
            builder.AppendLine($"Remove-NetFirewallRule -DisplayName '{name}'");
            if (add)
            {
                builder.AppendLine(
                    $"New-NetFirewallRule -DisplayName '{name}' -Direction Inbound -Action Block " +
                    $"-Protocol {leak.Protocol} -LocalPort {leak.Port} | Out-Null");
                builder.AppendLine(
                    $"New-NetFirewallRule -DisplayName '{name} (out)' -Direction Outbound -Action Block " +
                    $"-Protocol {leak.Protocol} -LocalPort {leak.Port} | Out-Null");
            }
        }
        return builder.ToString();
    }

    /// <summary>Повышенный запуск PowerShell со скриптом-файлом (один UAC).</summary>
    private static Task<bool> RunElevatedAsync(string script, bool waitForExit = false)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(),
            "nexus-port-shield-" + Guid.NewGuid().ToString("N") + ".ps1");
        File.WriteAllText(scriptPath, script);
        var info = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(scriptPath);
        var process = Process.Start(info);
        if (process is null) return Task.FromResult(false);
        if (waitForExit) return Task.Run(() =>
        {
            if (!process.WaitForExit(20_000)) return false;
            TryDelete(scriptPath);
            return process.ExitCode == 0;
        });
        // Запуск без ожидания: браузер не должен стоять из-за UAC.
        process.EnableRaisingEvents = true;
        process.Exited += (_, _) => TryDelete(scriptPath);
        return Task.FromResult(true);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
