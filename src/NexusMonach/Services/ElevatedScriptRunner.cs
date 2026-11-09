using System.Diagnostics;

namespace NexusMonach.Services;

/// <summary>
/// Единый скрытый повышенный запуск PowerShell-скрипта (conhost --headless,
/// файл со случайным именем): окно консоли не появляется физически, виден
/// только честный системный диалог UAC. Один проверенный механизм на все
/// защитные действия браузера (ARP-страж и др.).
/// </summary>
internal static class ElevatedScriptRunner
{
    public static async Task<bool> RunAsync(string script, string namePrefix)
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
            try { File.Delete(scriptPath); } catch { }
            return process.ExitCode == 0;
        });
    }
}
