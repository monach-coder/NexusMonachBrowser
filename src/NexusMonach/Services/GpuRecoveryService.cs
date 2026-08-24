using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace NexusMonach.Services;

/// <summary>
/// Самовосстановление полного режима после сбоя графики. В осторожном режиме
/// (GPU выключен прошлым сбоем) периодически щупает видеокарту скрытым окном
/// на аппаратной отрисовке: серия здоровых проб — голосовое оповещение,
/// маркер восстановления для Guardian и перезапуск в полный боевой режим.
/// </summary>
public static class GpuRecoveryService
{
    private const int HealthyProbesRequired = 3;
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(75);
    // Хвост после пробы: поздние SyncFlush от закрываемого окна тоже считаются
    // частью пробы и не должны запускать аварийный перезапуск.
    private static readonly TimeSpan ProbeTail = TimeSpan.FromSeconds(3);

    private static DispatcherTimer? _timer;
    private static Window? _probeWindow;
    private static int _healthyProbes;
    private static int _recoveryRestartStarted;
    private static volatile bool _probeInProgress;

    /// <summary>Идёт ли прямо сейчас аппаратная проба — для маршрутизации сбоев рендера.</summary>
    public static bool ProbeInProgress => _probeInProgress;

    /// <summary>Останавливает пробы — вызывается при штатном выходе браузера.</summary>
    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
        try { _probeWindow?.Close(); } catch { }
        _probeWindow = null;
    }

    /// <summary>Запускает пробы, только если браузер в осторожном режиме без GPU.</summary>
    public static void StartIfCautiousMode()
    {
        if (!GuardianRuntime.DisableGpuOnly || GuardianRuntime.IsSafeMode) return;
        if (_timer is not null) return;
        _timer = new DispatcherTimer { Interval = ProbeInterval };
        _timer.Tick += (_, _) => _ = RunProbeAsync();
        _timer.Start();
        CrashReportService.AddBreadcrumb("gpu-recovery", "probing-started");
    }

    private static async Task RunProbeAsync()
    {
        if (_probeInProgress) return;
        _probeInProgress = true;
        try
        {
            // Скрытое за пределами экрана окно на аппаратном рендеринге:
            // несколько перекомпоновок без исключений — признак живого драйвера.
            RenderOptions.ProcessRenderMode = RenderMode.Default;
            var window = new Window
            {
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -32000,
                Top = -32000,
                Width = 160,
                Height = 120,
                Content = new Border
                {
                    Background = Brushes.DarkSlateBlue,
                    CornerRadius = new CornerRadius(8),
                    Child = new StackPanel { Margin = new Thickness(12) }
                }
            };
            _probeWindow = window;
            window.Show();
            for (var frame = 0; frame < 3; frame++)
            {
                await Application.Current.Dispatcher.InvokeAsync(
                    () => window.Width += 1, DispatcherPriority.Render);
                await Task.Delay(150);
            }
            window.Close();
            _probeWindow = null;

            await Task.Delay(ProbeTail);

            _healthyProbes++;
            CrashReportService.AddBreadcrumb("gpu-recovery", "probe-healthy-" + _healthyProbes);
            if (_healthyProbes >= HealthyProbesRequired)
                BeginRecoveryRestart();
        }
        catch (Exception ex)
        {
            ProbeFailed(ex);
        }
        finally
        {
            RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
            _probeInProgress = false;
        }
    }

    /// <summary>
    /// Вызывается обработчиком dispatcher-исключений, когда гибель потока
    /// рендеринга случилась во время пробы: это значит, драйвер ещё не ожил.
    /// Счётчик здоровья сбрасывается, режим остаётся осторожным.
    /// </summary>
    public static void NotifyProbeRenderFailure()
    {
        _healthyProbes = 0;
        try { _probeWindow?.Close(); } catch { }
        _probeWindow = null;
        CrashReportService.AddBreadcrumb("gpu-recovery", "probe-render-failure");
    }

    private static void ProbeFailed(Exception exception)
    {
        _healthyProbes = 0;
        try { _probeWindow?.Close(); } catch { }
        _probeWindow = null;
        CrashReportService.RecordNonFatal("gpu-recovery", "probe-failed", exception);
    }

    private static void BeginRecoveryRestart()
    {
        if (Interlocked.Exchange(ref _recoveryRestartStarted, 1) != 0) return;
        _timer?.Stop();
        CrashReportService.AddBreadcrumb("gpu-recovery", "restoring-full-mode");
        try
        {
            VoiceAssistantService.Announce(
                "Графика восстановилась. Перезапускаю браузер в полный режим.",
                VoiceAnnouncementPriority.Important);
        }
        catch { /* Озвучка не должна мешать восстановлению. */ }
        WriteRecoveryMarker();
        _ = Task.Run(async () =>
        {
            // Даём фразе прозвучать до остановки сервисов в OnExit.
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            try
            {
                var root = AppContext.BaseDirectory;
                var guardian = Path.Combine(root, "NexusMonach.exe");
                if (File.Exists(guardian))
                {
                    var info = new ProcessStartInfo(guardian)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = root
                    };
                    info.ArgumentList.Add("--wait-for-previous-instance");
                    Process.Start(info);
                }
                else if (Environment.ProcessPath is { } browser)
                {
                    Process.Start(new ProcessStartInfo(browser) { UseShellExecute = true });
                }
            }
            catch { /* Не стартовало — остаёмся в текущем режиме. */ }
            try { Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown(0)); }
            catch { Environment.Exit(0); }
        });
    }

    /// <summary>
    /// Маркер для Guardian: сбои графики старше момента восстановления
    /// не считаются — следующий запуск будет полным, а не осторожным.
    /// </summary>
    private static void WriteRecoveryMarker()
    {
        try
        {
            var guardianRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NexusMonach", "Guardian");
            Directory.CreateDirectory(guardianRoot);
            File.WriteAllText(Path.Combine(guardianRoot, "gpu-recovery.json"),
                JsonSerializer.Serialize(new { recoveredAtUtc = DateTimeOffset.UtcNow }));
        }
        catch { /* Маркер не критичен: окно 30 минут всё равно закроется. */ }
    }
}
