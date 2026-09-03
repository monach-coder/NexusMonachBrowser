using System.Diagnostics;
using System.Runtime.InteropServices;
using NexusMonach.Models;

namespace NexusMonach.Services;

public enum DownloadScanState
{
    None,
    Scanning,
    Clean,
    Threat,
    Unavailable
}

/// <summary>
/// Проверка завершённых загрузок активным антивирусом машины — через AMSI
/// (Защитник ИЛИ сторонний продукт: Doctor Web, Kaspersky и т.д.), файл
/// читается с FileShare.ReadWrite, поэтому гонки «файл ещё держит загрузчик»,
/// портившие статус на живой машине, невозможны по построению. Большие файлы
/// идут через официальный CLI Защитника (MpCmdRun) с одним повтором на
/// мимолётный сбой. Скан локальный: ни сети, ни отправки файлов.
/// </summary>
public static class AntivirusScanService
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(120);
    private const long AmsiMaxBytes = 64 * 1024 * 1024;

    internal static DownloadScanState Classify(int exitCode, string output)
    {
        if (exitCode == 0) return DownloadScanState.Clean;
        // MpCmdRun returns 2 for detections; a disabled service can also fail
        // with non-zero codes, so a detection is confirmed by its own report.
        if (exitCode == 2)
        {
            var text = output.ToLowerInvariant();
            return text.Contains("error") || text.Contains("disabled")
                ? DownloadScanState.Unavailable
                : DownloadScanState.Threat;
        }
        return DownloadScanState.Unavailable;
    }

    public static string ScanStatusText(DownloadScanState state) => state switch
    {
        DownloadScanState.Clean => "Антивирус: угроз не обнаружено",
        DownloadScanState.Threat => "Антивирус: обнаружена угроза",
        DownloadScanState.Scanning => "Проверка антивирусом…",
        DownloadScanState.Unavailable => "Антивирус: не удалось проверить",
        _ => "Антивирус: ожидание проверки"
    };

    public static async Task ScanAsync(DownloadItem item)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(item.FilePath))
        {
            SetState(item, DownloadScanState.Unavailable);
            return;
        }

        SetState(item, DownloadScanState.Scanning);

        // Компактные файлы — через AMSI: любой активный антивирус,
        // без блокировки файла и без запуска внешних процессов.
        try
        {
            if (new FileInfo(item.FilePath).Length <= AmsiMaxBytes)
            {
                var amsi = await Task.Run(() => ScanByAmsi(item.FilePath));
                if (amsi is not null)
                {
                    SetState(item, amsi.Value);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("downloads", "antivirus-amsi", ex);
        }

        await ScanByDefenderCliAsync(item);
    }

    /// <summary>AMSI-результат; null — интерфейс недоступен, нужен запасной путь.</summary>
    private static DownloadScanState? ScanByAmsi(string filePath)
    {
        if (AmsiInitialize("NexusMonach", out var context) != 0)
            return null;
        try
        {
            if (AmsiOpenSession(context, out var session) != 0)
                return null;
            try
            {
                // ReadWrite+Delete: загрузка могла ещё не отпустить дескриптор.
                using var stream = new FileStream(filePath, FileMode.Open,
                    FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var memory = new MemoryStream((int)stream.Length);
                stream.CopyTo(memory);
                var bytes = memory.ToArray();
                var hr = AmsiScanBuffer(context, bytes, (uint)bytes.Length,
                    filePath, session, out var result);
                if (hr != 0)
                    return null;
                return result switch
                {
                    AmsiResult.Clean or AmsiResult.NotDetected => DownloadScanState.Clean,
                    AmsiResult.Detected => DownloadScanState.Threat,
                    _ => DownloadScanState.Unavailable
                };
            }
            finally
            {
                AmsiCloseSession(context, session);
            }
        }
        finally
        {
            AmsiUninitialize(context);
        }
    }

    private static async Task ScanByDefenderCliAsync(DownloadItem item)
    {
        var executable = FindMpCmdRun();
        if (executable is null)
        {
            SetState(item, DownloadScanState.Unavailable);
            return;
        }

        // Один повтор: мимолётная занятость файла — не отсутствие антивируса.
        // Повторяем только БЫСТРЫЙ неуспех; долгий таймаут-выход бессмысленно
        // повторять целиком.
        for (var attempt = 0; ; attempt++)
        {
            var started = Stopwatch.StartNew();
            var state = await RunDefenderScanAsync(executable, item.FilePath);
            var fastFailure = started.Elapsed < TimeSpan.FromSeconds(5);
            if (state != DownloadScanState.Unavailable || attempt >= 1 || !fastFailure)
            {
                SetState(item, state);
                return;
            }
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }

    private static async Task<DownloadScanState> RunDefenderScanAsync(string executable, string filePath)
    {
        try
        {
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add("-Scan");
            start.ArgumentList.Add("-ScanType");
            start.ArgumentList.Add("3");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(filePath);

            using var process = Process.Start(start);
            if (process is null)
                return DownloadScanState.Unavailable;

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(ScanTimeout);
            try
            {
                while (!process.WaitForExit(500))
                {
                    if (timeout.IsCancellationRequested)
                    {
                        try { if (!process.HasExited) process.Kill(true); } catch { }
                        return DownloadScanState.Unavailable;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // The process already exited between WaitForExit calls.
            }

            var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            return Classify(process.ExitCode, output);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("downloads", "antivirus-scan", ex);
            return DownloadScanState.Unavailable;
        }
    }

    private static void SetState(DownloadItem item, DownloadScanState state) =>
        Ui.Invoke(() => item.ScanState = state);

    private static string? FindMpCmdRun()
    {
        string[] roots =
        [
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.SystemX86)
        ];
        foreach (var root in roots.Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            var candidate = Path.Combine(root, "Windows Defender", "MpCmdRun.exe");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private enum AmsiResult
    {
        Clean = 0,
        NotDetected = 1,
        BlockedByAdminStart = 0x4000,
        Detected = 0x8000
    }

    [DllImport("amsi.dll", CharSet = CharSet.Unicode, EntryPoint = "AmsiInitialize")]
    private static extern int AmsiInitialize(string appName, out IntPtr amsiContext);

    [DllImport("amsi.dll", EntryPoint = "AmsiOpenSession")]
    private static extern int AmsiOpenSession(IntPtr amsiContext, out IntPtr session);

    [DllImport("amsi.dll", CharSet = CharSet.Unicode, EntryPoint = "AmsiScanBuffer")]
    private static extern int AmsiScanBuffer(IntPtr amsiContext, byte[] buffer, uint length,
        string contentName, IntPtr session, out AmsiResult result);

    [DllImport("amsi.dll", EntryPoint = "AmsiCloseSession")]
    private static extern void AmsiCloseSession(IntPtr amsiContext, IntPtr session);

    [DllImport("amsi.dll", EntryPoint = "AmsiUninitialize")]
    private static extern void AmsiUninitialize(IntPtr amsiContext);
}
