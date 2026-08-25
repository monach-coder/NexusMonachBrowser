namespace NexusMonach.Services;

/// <summary>
/// Журнал проглоченных исключений. Пустые catch — молчаливые отказы:
/// страница не перевелась, текст не прочитался, но никто не узнал.
/// Теперь каждая точка оставляет след в breadcrumbs краш-рапорта —
/// причинный граф видит, что именно глохло, ещё до аварии.
/// </summary>
public static class SwallowLog
{
    /// <summary>
    /// Регистрирует проглоченное исключение. Дешёвая операция:
    /// постановка в ограниченную очередь breadcrumbs, без ввода-вывода.
    /// </summary>
    public static void Log(string component, string context, Exception? exception = null) =>
        CrashReportService.AddBreadcrumb(component,
            "swallow:" + context + (exception is null ? string.Empty : ":" + exception.GetType().Name));
}
