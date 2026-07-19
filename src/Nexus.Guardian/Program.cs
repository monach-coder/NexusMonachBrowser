using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace Nexus.Guardian;

internal static class Program
{
    private static readonly string GuardianRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusMonach", "Guardian");

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0].Equals("--generate-key", StringComparison.OrdinalIgnoreCase))
            {
                IntegrityVerifier.GenerateKeyPair(args.Length > 1 ? args[1] : Environment.CurrentDirectory);
                return 0;
            }

            if (args.Length > 1 && args[0].Equals("--create-manifest", StringComparison.OrdinalIgnoreCase))
            {
                var keyIndex = Array.FindIndex(args, x => x.Equals("--private-key", StringComparison.OrdinalIgnoreCase));
                IntegrityVerifier.CreateManifest(args[1], keyIndex >= 0 && keyIndex + 1 < args.Length ? args[keyIndex + 1] : null);
                return 0;
            }

            if (args.Length > 0 && args[0].Equals("--verify-only", StringComparison.OrdinalIgnoreCase))
            {
                var root = args.Length > 1 && !args[1].StartsWith("--", StringComparison.Ordinal)
                    ? args[1] : AppContext.BaseDirectory;
                var result = IntegrityVerifier.Verify(root,
                    args.Any(x => x.Equals("--full-integrity-check", StringComparison.OrdinalIgnoreCase)));
                Console.WriteLine(result.CompactStatus);
                foreach (var problem in result.Problems) Console.WriteLine(problem);
                return result.State == IntegrityState.Verified ? 0 : 4;
            }

            var full = args.Any(x => x.Equals("--full-integrity-check", StringComparison.OrdinalIgnoreCase));
            return LaunchBrowser(full, args.Where(x => !x.Equals("--full-integrity-check", StringComparison.OrdinalIgnoreCase)).ToArray());
        }
        catch (Exception ex)
        {
            MessageBox.Show("Nexus Guardian не смог выполнить безопасный запуск.\n\n" + ex.Message,
                "Nexus Guardian", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 70;
        }
    }

    private static int LaunchBrowser(bool full, string[] forwardedArgs)
    {
        var root = AppContext.BaseDirectory;
        var browser = Path.Combine(root, "NexusMonach.Browser.exe");
        if (!File.Exists(browser))
        {
            MessageBox.Show("Не найден NexusMonach.Browser.exe. Переустановите браузер из официального архива.",
                "Nexus Guardian", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }

        var integrity = IntegrityVerifier.Verify(root, full);
        WriteIntegrityIncident(integrity);
        if (!integrity.CanLaunch)
        {
            MessageBox.Show("Запуск заблокирован: нарушена целостность критических файлов.\n\n" +
                            string.Join("\n", integrity.Problems.Take(8)) +
                            "\n\nСкачайте официальный архив заново. Guardian не будет запускать изменённый браузер.",
                "Nexus Guardian", MessageBoxButtons.OK, MessageBoxIcon.Stop);
            return 3;
        }

        var safeMode = ShouldUseSafeMode();
        if (integrity.State == IntegrityState.NonCriticalMismatch)
        {
            safeMode = true;
            MessageBox.Show("Некритические файлы или локальные модели изменены. Браузер будет открыт в безопасном режиме без AI и расширений.\n\n" +
                            string.Join("\n", integrity.Problems.Take(6)), "Nexus Guardian",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        else if (integrity.State == IntegrityState.DevelopmentBuild)
        {
            MessageBox.Show("Это локальная сборка без подписанного манифеста целостности. Для тестирования запуск разрешён, но статус Guardian будет «не проверено».",
                "Nexus Guardian", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        Directory.CreateDirectory(Path.Combine(GuardianRoot, "Sessions"));
        var sessionId = Guid.NewGuid().ToString("N");
        var info = new ProcessStartInfo(browser)
        {
            UseShellExecute = false,
            WorkingDirectory = root
        };
        foreach (var arg in forwardedArgs) info.ArgumentList.Add(arg);
        info.Environment["NEXUS_GUARDIAN_SESSION"] = sessionId;
        info.Environment["NEXUS_INTEGRITY_STATUS"] = integrity.CompactStatus;
        info.Environment["NEXUS_SAFE_MODE"] = safeMode ? "1" : "0";

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Windows не создал процесс браузера.");
        process.WaitForExit();
        var clean = ReadCleanSession(sessionId);
        var normalExit = process.ExitCode == 0 && clean;
        RecordExit(normalExit);
        if (!normalExit) WriteNativeCrashReport(sessionId, process.ExitCode, integrity.CompactStatus, safeMode);
        return process.ExitCode;
    }

    private static bool ShouldUseSafeMode()
    {
        var state = ReadCrashState();
        var threshold = DateTimeOffset.UtcNow.AddMinutes(-10);
        state.AbnormalExitsUtc.RemoveAll(x => x < threshold);
        return state.AbnormalExitsUtc.Count >= 3;
    }

    private static void RecordExit(bool clean)
    {
        var state = ReadCrashState();
        var threshold = DateTimeOffset.UtcNow.AddMinutes(-10);
        state.AbnormalExitsUtc.RemoveAll(x => x < threshold);
        if (clean) state.AbnormalExitsUtc.Clear();
        else state.AbnormalExitsUtc.Add(DateTimeOffset.UtcNow);
        Directory.CreateDirectory(GuardianRoot);
        File.WriteAllText(Path.Combine(GuardianRoot, "crash-state.json"), JsonSerializer.Serialize(state));
    }

    private static GuardianCrashState ReadCrashState()
    {
        try
        {
            var path = Path.Combine(GuardianRoot, "crash-state.json");
            return File.Exists(path)
                ? JsonSerializer.Deserialize<GuardianCrashState>(File.ReadAllText(path)) ?? new GuardianCrashState()
                : new GuardianCrashState();
        }
        catch { return new GuardianCrashState(); }
    }

    private static bool ReadCleanSession(string sessionId)
    {
        var path = Path.Combine(GuardianRoot, "Sessions", sessionId + ".json");
        try
        {
            if (!File.Exists(path)) return false;
            var result = JsonSerializer.Deserialize<GuardianSessionResult>(File.ReadAllText(path));
            File.Delete(path);
            return result?.CleanExit == true;
        }
        catch { return false; }
    }

    private static void WriteNativeCrashReport(string sessionId, int exitCode, string integrityStatus, bool safeMode)
    {
        try
        {
            var vault = Path.Combine(GuardianRoot, "CrashVault");
            Directory.CreateDirectory(vault);
            var payload = new
            {
                SchemaVersion = 1,
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTimeOffset.UtcNow,
                Fatal = true,
                BrowserVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                OsVersion = Environment.OSVersion.VersionString,
                WebView2Version = "unavailable-after-process-exit",
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Component = "native-process",
                Stage = "browser-exit",
                ExceptionType = "ProcessExit",
                Message = $"Browser process ended without a clean Guardian session marker. Exit code: {exitCode}.",
                StackTrace = string.Empty,
                IntegrityStatus = integrityStatus,
                SafeMode = safeMode,
                GuardianSession = sessionId
            };
            var path = Path.Combine(vault, $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{payload.Id}.pending.json");
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static void WriteIntegrityIncident(IntegrityResult integrity)
    {
        if (integrity.State is IntegrityState.Verified or IntegrityState.DevelopmentBuild) return;
        try
        {
            Directory.CreateDirectory(GuardianRoot);
            var signature = integrity.CompactStatus + "|" + string.Join("|", integrity.Problems.Take(20));
            var statePath = Path.Combine(GuardianRoot, "last-integrity-incident.json");
            if (File.Exists(statePath))
            {
                var previous = JsonSerializer.Deserialize<GuardianIntegrityIncidentState>(File.ReadAllText(statePath));
                if (previous?.Signature == signature && previous.TimestampUtc > DateTimeOffset.UtcNow.AddDays(-1))
                    return;
            }

            File.WriteAllText(statePath, JsonSerializer.Serialize(new GuardianIntegrityIncidentState
            {
                Signature = signature,
                TimestampUtc = DateTimeOffset.UtcNow
            }));

            var vault = Path.Combine(GuardianRoot, "CrashVault");
            Directory.CreateDirectory(vault);
            var id = Guid.NewGuid().ToString("N");
            var timestamp = DateTimeOffset.UtcNow;
            var message = string.Join("; ", integrity.Problems.Take(12));
            if (message.Length > 4000) message = message[..4000] + "…";
            var payload = new
            {
                SchemaVersion = 1,
                Id = id,
                TimestampUtc = timestamp,
                Fatal = !integrity.CanLaunch,
                BrowserVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown",
                OsVersion = Environment.OSVersion.VersionString,
                WebView2Version = "not-started",
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Component = "guardian-integrity",
                Stage = "preflight",
                ExceptionType = "IntegrityViolation",
                Message = message,
                StackTrace = string.Empty,
                IntegrityStatus = integrity.CompactStatus,
                SafeMode = integrity.State == IntegrityState.NonCriticalMismatch
            };
            var path = Path.Combine(vault, $"{timestamp:yyyyMMdd-HHmmss}-{id}.pending.json");
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* Integrity reporting must never weaken or block verification. */ }
    }
}
