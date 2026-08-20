using System.Diagnostics;
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
/// Scans completed downloads with the built-in Windows Defender via its
/// official MpCmdRun CLI. The scan is local: no network endpoints, no uploads.
/// When Defender is absent or its service is disabled the item is marked as
/// unchecked instead of failing the download flow.
/// </summary>
public static class AntivirusScanService
{
    private static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(120);

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
        DownloadScanState.Unavailable => "Антивирус: проверка недоступна",
        _ => "Антивирус: ожидание проверки"
    };

    public static async Task ScanAsync(DownloadItem item)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(item.FilePath))
        {
            SetState(item, DownloadScanState.Unavailable);
            return;
        }

        var executable = FindMpCmdRun();
        if (executable is null)
        {
            SetState(item, DownloadScanState.Unavailable);
            return;
        }

        SetState(item, DownloadScanState.Scanning);
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
            start.ArgumentList.Add(item.FilePath);

            using var process = Process.Start(start);
            if (process is null)
            {
                SetState(item, DownloadScanState.Unavailable);
                return;
            }

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
                        SetState(item, DownloadScanState.Unavailable);
                        return;
                    }
                }
            }
            catch (InvalidOperationException)
            {
                // The process already exited between WaitForExit calls.
            }

            var output = await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false);
            SetState(item, Classify(process.ExitCode, output));
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("downloads", "antivirus-scan", ex);
            SetState(item, DownloadScanState.Unavailable);
        }
    }

    private static void SetState(DownloadItem item, DownloadScanState state) =>
        System.Windows.Application.Current.Dispatcher.Invoke(() => item.ScanState = state);

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
}
