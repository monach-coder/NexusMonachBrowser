using System.Diagnostics;

namespace NexusMonach.Services;

/// <summary>
/// Детект GoodbyeDPI (ValdikSS): обходчик DPI работает на уровне драйвера
/// WinDivert и делает часть заблокированных сайтов достижимыми в прямом
/// маршруте. Браузер ничего не устанавливает и не запускает сам — только
/// видит сторонний инструмент и сообщает голосом.
/// </summary>
public static class DpiBypassDetector
{
    private static DateTimeOffset _checkedAt = DateTimeOffset.MinValue;
    private static bool _cached;

    /// <summary>GoodbyeDPI запущен (кэш 30 секунд — путь горячего соединения не трогаем).</summary>
    public static bool IsRunning
    {
        get
        {
            if (DateTimeOffset.UtcNow - _checkedAt < TimeSpan.FromSeconds(30))
                return _cached;
            _cached = Detect();
            _checkedAt = DateTimeOffset.UtcNow;
            return _cached;
        }
    }

    private static bool Detect()
    {
        try
        {
            foreach (var process in Process.GetProcessesByName("goodbyedpi"))
            {
                process.Dispose();
                return true;
            }
        }
        catch
        {
            // Доступ к списку процессов недоступен — считаем выключенным.
        }
        return false;
    }
}
