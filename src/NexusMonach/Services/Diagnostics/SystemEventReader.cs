using System.Diagnostics.Eventing.Reader;
using System.Text;

namespace NexusMonach.Services.Diagnostics;

/// <summary>
/// Читает недавние системные события Windows, важные для диагностики отказов
/// браузера: краши процессов (Application, EventID 1000) и восстановления
/// драйвера графики (System, EventID 4101). Работает без прав администратора.
/// </summary>
public static class SystemEventReader
{
    public const string KindAppCrash = "app-crash";
    public const string KindDisplayDriverReset = "display-driver-reset";
    public const string KindDwmCrash = "dwm-crash";

    private static readonly TimeSpan MaxWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Возвращает события за последние <paramref name="window"/> минут.
    /// Никогда не бросает исключений: сбой чтения журнала не должен мешать
    /// записи самого краш-рапорта.
    /// </summary>
    public static IReadOnlyList<SystemEventRecord> ReadRecent(TimeSpan window)
    {
        if (!OperatingSystem.IsWindows())
            return [];
        if (window > MaxWindow) window = MaxWindow;
        var sinceUtc = DateTimeOffset.UtcNow - window;
        var result = new List<SystemEventRecord>();
        try
        {
            ReadApplicationCrashes(sinceUtc, result);
            ReadDisplayDriverResets(sinceUtc, result);
        }
        catch
        {
            // Журнал недоступен или читается медленно — тихо оставляем то, что успели.
        }
        return result;
    }

    private static void ReadApplicationCrashes(DateTimeOffset sinceUtc, List<SystemEventRecord> result)
    {
        var xpath = "*[System[(EventID=1000) and TimeCreated[timediff(@SystemTime) <= " +
                    (long)(DateTimeOffset.UtcNow - sinceUtc).TotalMilliseconds + "]]]";
        using var reader = Read("Application", xpath);
        while (reader.ReadEvent() is { } evt)
        {
            // Формат EventID 1000: [0]=имя сбойного приложения, [6]=код исключения.
            var app = Property(evt, 0);
            if (string.IsNullOrWhiteSpace(app)) continue;
            var code = Property(evt, 6);
            var details = string.IsNullOrWhiteSpace(code) ? string.Empty : $"код {code}";
            var kind = app.Contains("dwm", StringComparison.OrdinalIgnoreCase)
                ? KindDwmCrash
                : KindAppCrash;
            result.Add(new SystemEventRecord(kind, $"Крах процесса: {app}",
                evt.TimeCreated ?? sinceUtc, details));
        }
    }

    private static void ReadDisplayDriverResets(DateTimeOffset sinceUtc, List<SystemEventRecord> result)
    {
        // 4101 — «Видеоадаптер перестал отвечать и был успешно восстановлен».
        var xpath = "*[System[(EventID=4101) and TimeCreated[timediff(@SystemTime) <= " +
                    (long)(DateTimeOffset.UtcNow - sinceUtc).TotalMilliseconds + "]]]";
        using var reader = Read("System", xpath);
        while (reader.ReadEvent() is { } evt)
        {
            result.Add(new SystemEventRecord(KindDisplayDriverReset,
                $"Сброс видеоадаптера: {evt.ProviderName}",
                evt.TimeCreated ?? sinceUtc, "драйвер графики восстанавливался системой"));
        }
    }

    private static EventLogReader Read(string logName, string xpath) =>
        new(new EventLogQuery(logName, PathType.LogName, xpath) { ReverseDirection = true });

    private static string Property(EventRecord evt, int index) =>
        evt.Properties.Count > index ? evt.Properties[index].Value?.ToString() ?? string.Empty : string.Empty;
}
