using System.Diagnostics;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>Режим автоматической защиты портов при запуске браузера.</summary>
public enum PortShieldMode
{
    /// <summary>Сканировать и закрывать утечки файрволом на время сессии.</summary>
    Auto,
    /// <summary>Сканировать и сообщать голосом/в Дозоре, ничего не закрывать.</summary>
    NotifyOnly,
    /// <summary>Не сканировать автоматически.</summary>
    Off
}

/// <summary>
/// Порт-щит: при старте браузера сканирует машину и закрывает «утекающие»
/// порты локальной сети — mDNS, SSDP/UPnP и NetBIOS — правилами файрвола
/// Windows НА ВРЕМЯ СЕССИИ, снимая их при выходе. Консольное окно не
/// появляется никогда: скрипт выполняется через conhost --headless.
/// Если браузер аварийно умер и правила остались — следующий старт видит
/// их (чтение правил не требует прав администратора) и просто принимает
/// владение, не спрашивая повторного подтверждения. Порты пользовательских
/// служб (RDP, SMB, VNC) щит НЕ трогает.
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
    private static volatile bool _ownsSessionRules;

    /// <summary>
    /// Запускается при старте браузера: скан + (в режиме Auto) закрытие утечек
    /// на сессию. Никогда не бросает исключений и не блокирует запуск.
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

                // Правила уже стоят (прошлая сессия без чистого выхода) —
                // принимаем владение молча, без повторного запроса.
                if (await AreRulesAppliedAsync())
                {
                    _ownsSessionRules = true;
                    CrashReportService.AddBreadcrumb("port-shield", "adopted-existing");
                    Ui.Post(() => VoiceAssistantService.Announce(
                        "Порт-щит активен. Закрыты: " + names + ".",
                        VoiceAnnouncementPriority.Important));
                    return;
                }
                var applied = await ApplySessionShieldAsync();
                if (applied)
                {
                    _ownsSessionRules = true;
                    Ui.Post(() => VoiceAssistantService.Announce(
                        "Порт-щит активен. Закрыты на сессию: " + names + ".",
                        VoiceAnnouncementPriority.Important));
                }
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

    /// <summary>Снятие правил при выходе: только если эта сессия ими владеет.</summary>
    public static void RemoveSessionShield()
    {
        if (!_ownsSessionRules) return;
        _ownsSessionRules = false;
        try
        {
            _ = RunElevatedHiddenAsync(BuildRuleScript(AutoClosedLeaks.ToList(), add: false), "nexus-port-shield");
            try { File.Delete(StatePath); } catch { }
            CrashReportService.AddBreadcrumb("port-shield", "session-removed");
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("port-shield", "remove", ex);
        }
    }

    /// <summary>
    /// Чтение правила файрвола не требует прав администратора: тихая
    /// проверка «правила уже стоят?» одним вызовом netsh.
    /// </summary>
    private static async Task<bool> AreRulesAppliedAsync()
    {
        try
        {
            var info = new ProcessStartInfo("netsh.exe")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true
            };
            info.ArgumentList.Add("advfirewall");
            info.ArgumentList.Add("firewall");
            info.ArgumentList.Add("show");
            info.ArgumentList.Add("rule");
            info.ArgumentList.Add("name=" + RuleName(AutoClosedLeaks[0]));
            using var process = Process.Start(info);
            if (process is null) return false;
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return output.Contains("Enabled:", StringComparison.Ordinal) ||
                   output.Contains("Включено:", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Применяет правила на сессию одним скрытым повышенным вызовом.</summary>
    private static async Task<bool> ApplySessionShieldAsync()
    {
        var ok = await RunElevatedHiddenAsync(BuildRuleScript(AutoClosedLeaks.ToList(), add: true), "nexus-port-shield");
        if (ok)
        {
            await File.WriteAllTextAsync(StatePath,
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    appliedUtc = DateTimeOffset.UtcNow,
                    mode = "session"
                }));
            CrashReportService.AddBreadcrumb("port-shield", "applied");
        }
        else
        {
            CrashReportService.AddBreadcrumb("port-shield", "apply-declined");
        }
        return ok;
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
            builder.AppendLine($"Remove-NetFirewallRule -DisplayName '{name} (out)'");
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

    /// <summary>
    /// Единый скрытый повышенный запуск скрипта (conhost --headless, файл со
    /// случайным именем). Используется порт-щитом и ARP-стражем: один
    /// проверенный механизм на все защитные действия.
    /// </summary>
    internal static Task<bool> RunElevatedScript(string script, string namePrefix) =>
        RunElevatedHiddenAsync(script, namePrefix);

    /// <summary>
    /// Повышенный запуск PowerShell без окна: conhost --headless запрещает
    /// создание консольного окна физически. Виден только диалог UAC —
    /// это системное разрешение на изменение файрвола, и оно честно.
    /// </summary>
    private static async Task<bool> RunElevatedHiddenAsync(string script, string namePrefix)
    {
        var scriptPath = Path.Combine(Path.GetTempPath(),
            namePrefix + "-" + Guid.NewGuid().ToString("N") + ".ps1");
        await File.WriteAllTextAsync(scriptPath, script);
        var conhost = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "conhost.exe");
        var info = new ProcessStartInfo(File.Exists(conhost) ? conhost : "powershell.exe")
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        if (File.Exists(conhost))
        {
            info.ArgumentList.Add("--headless");
            info.ArgumentList.Add("powershell.exe");
        }
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(scriptPath);
        var process = Process.Start(info);
        if (process is null) return false;
        return await Task.Run(() =>
        {
            if (!process.WaitForExit(30_000)) return false;
            TryDelete(scriptPath);
            return process.ExitCode == 0;
        });
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
