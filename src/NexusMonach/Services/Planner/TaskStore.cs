using System.Text.Json;

namespace NexusMonach.Services.Planner;

public enum TaskStatus { Open, Done, Cancelled }

/// <summary>
/// Задача планировщика. Хранится локально и (для задач из чата) связана
/// с источником; сроки — необязательны, экспорт — .ics и mailto.
/// </summary>
public sealed class PlannerTask
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public TaskStatus Status { get; set; } = TaskStatus.Open;
    public DateTimeOffset? DueUtc { get; set; }
    /// <summary>Откуда задача: «вручную» или «чат:ид-комнаты».</summary>
    public string Source { get; set; } = "вручную";
    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DoneUtc { get; set; }
}

/// <summary>
/// Локальное хранилище задач планировщика (Data/planner-tasks.json).
/// Задачи — персональные данные пользователя, никуда не отправляются.
/// </summary>
public static class TaskStore
{
    private static readonly object Gate = new();
    private static List<PlannerTask> _tasks = [];
    private static bool _loaded;

    public static event Action? Changed;

    private static string StorePath => Path.Combine(AppPaths.AppRoot, "planner-tasks.json");

    public static IReadOnlyList<PlannerTask> All
    {
        get { EnsureLoaded(); lock (Gate) { return _tasks.ToList(); } }
    }

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(StorePath))
                    _tasks = JsonSerializer.Deserialize<List<PlannerTask>>(
                        File.ReadAllText(StorePath)) ?? [];
            }
            catch (Exception ex)
            {
                CrashReportService.RecordNonFatal("planner", "load", ex);
                _tasks = [];
            }
            _loaded = true;
        }
    }

    public static PlannerTask Add(string title, string notes = "", DateTimeOffset? due = null,
        string source = "вручную")
    {
        EnsureLoaded();
        var task = new PlannerTask
        {
            Title = title.Trim(),
            Notes = notes.Trim(),
            DueUtc = due,
            Source = source
        };
        lock (Gate)
        {
            _tasks.Add(task);
            Save();
        }
        Changed?.Invoke();
        return task;
    }

    public static void SetStatus(Guid id, TaskStatus status)
    {
        EnsureLoaded();
        lock (Gate)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task is null) return;
            task.Status = status;
            task.DoneUtc = status == TaskStatus.Done ? DateTimeOffset.UtcNow : null;
            Save();
        }
        Changed?.Invoke();
    }

    public static void Remove(Guid id)
    {
        EnsureLoaded();
        lock (Gate)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task is null) return;
            _tasks.Remove(task);
            Save();
        }
        Changed?.Invoke();
    }

    private static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath,
            JsonSerializer.Serialize(_tasks, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Экспорт календаря: задачи со сроками становятся событиями .ics —
    /// импортируются в Google/Outlook/любой календарь (интеграция по выбору
    /// пользователя, без облачных API).
    /// </summary>
    public static string BuildIcs(IEnumerable<PlannerTask> tasks)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("BEGIN:VCALENDAR");
        builder.AppendLine("VERSION:2.0");
        builder.AppendLine("PRODID:-//Nexus Monach//Planner//RU");
        foreach (var task in tasks.Where(t => t.DueUtc is not null && t.Status == TaskStatus.Open))
        {
            var stamp = task.DueUtc!.Value;
            builder.AppendLine("BEGIN:VEVENT");
            builder.AppendLine("UID:" + task.Id + "@nexus-monach");
            builder.AppendLine("DTSTAMP:" + IcsStamp(DateTimeOffset.UtcNow));
            builder.AppendLine("DTSTART:" + IcsStamp(stamp));
            builder.AppendLine("DTEND:" + IcsStamp(stamp.AddMinutes(30)));
            builder.AppendLine("SUMMARY:" + IcsEscape("[Nexus] " + task.Title));
            if (task.Notes.Length > 0)
                builder.AppendLine("DESCRIPTION:" + IcsEscape(task.Notes));
            builder.AppendLine("END:VEVENT");
        }
        builder.AppendLine("END:VCALENDAR");
        return builder.ToString();
    }

    /// <summary>mailto-ссылка с задачей: «отправить по почте» без почтового API.</summary>
    public static string BuildMailto(PlannerTask task, string recipient = "") =>
        "mailto:" + Uri.EscapeDataString(recipient) +
        "?subject=" + Uri.EscapeDataString("[Nexus] Задача: " + task.Title) +
        "&body=" + Uri.EscapeDataString(task.Title + "\n\n" + task.Notes +
            (task.DueUtc is { } due ? "\n\nСрок: " + due.ToLocalTime().ToString("yyyy-MM-dd HH:mm") : ""));

    private static string IcsStamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'");

    private static string IcsEscape(string value) =>
        value.Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,").Replace("\n", "\\n");
}
