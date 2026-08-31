using System.IO;

namespace NexusMonach.Services;

/// <summary>
/// Контроль свободного места на системном диске: при меньше 5 ГБ деградируют
/// нейроголос (нет места под wav-буферы), файл подкачки не растёт (тормоза
/// и вылеты), временные файлы падают с «No space left on device». Браузер
/// проверяет при старте и каждые 10 минут, предупреждая голосом.
/// </summary>
public static class DiskSpaceWatchdog
{
    private static Timer? _timer;
    private static DateTimeOffset _lastWarnedUtc = DateTimeOffset.MinValue;
    private static readonly TimeSpan WarnInterval = TimeSpan.FromMinutes(15);

    public static void Start()
    {
        if (_timer is not null) return;
        Check();
        _timer = new Timer(_ => Check(), null,
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    public static void Stop() => _timer?.Dispose();

    private static void Check()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.CurrentDirectory) ?? "C:\\");
            if (!drive.IsReady) return;
            var freeGb = drive.AvailableFreeSpace / 1024.0 / 1024 / 1024;
            if (freeGb < 5.0 &&
                DateTimeOffset.UtcNow - _lastWarnedUtc > WarnInterval)
            {
                _lastWarnedUtc = DateTimeOffset.UtcNow;
                Ui.Post(() =>
                {
                    VoiceAssistantService.Announce(
                        $"Внимание: на системном диске осталось {freeGb:F0} гигабайт. " +
                        "Голос и скорость деградируют. Освободите место.",
                        VoiceAnnouncementPriority.Critical);
                    CrashReportService.AddBreadcrumb("disk", $"low-space-{freeGb:F1}gb");
                });
            }
        }
        catch
        {
            // Контроль места не должен ронять браузер.
        }
    }
}
