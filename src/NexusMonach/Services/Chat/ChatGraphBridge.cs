using NexusMonach.Models;
using NexusMonach.Services.Planner;

namespace NexusMonach.Services.Chat;

/// <summary>
/// Потоковая выжимка переписки в долговременную память: маркеры в тексте
/// («задача:», «факт:», «решение:») превращаются в задачи планировщика и
/// узлы графа знаний СРАЗУ по ходу разговора — сессия испаряется, знания
/// остаются. Выжимка локальна: текст не покидает машину.
/// </summary>
public static class ChatGraphBridge
{
    /// <summary>Извлекает маркерные строки из сообщения. Чистая функция.</summary>
    public static IReadOnlyList<(string Kind, string Value)> ExtractMarkers(string text)
    {
        var results = new List<(string, string)>();
        foreach (var rawLine in (text ?? string.Empty).Split('\n'))
        {
            var line = rawLine.Trim();
            foreach (var marker in new[] { "задача:", "факт:", "решение:" })
            {
                if (!line.StartsWith(marker, StringComparison.OrdinalIgnoreCase)) continue;
                var value = line[marker.Length..].Trim();
                if (value.Length > 0)
                    results.Add((marker[..^1], value));
                break;
            }
        }
        return results;
    }

    /// <summary>
    /// Прогоняет сообщение через выжимку: задачи — в планировщик, факты и
    /// решения — в граф знаний с пометкой источника. Возвращает счётчик.
    /// </summary>
    public static (int Tasks, int Facts) Absorb(ChatMessage message, string roomName)
    {
        if (message.Text is null) return (0, 0);
        var tasks = 0;
        var facts = 0;
        var source = "чат: " + roomName;
        foreach (var (kind, value) in ExtractMarkers(message.Text))
        {
            switch (kind)
            {
                case "задача":
                    TaskStore.Add(value, "Из переписки «" + roomName + "», " +
                        message.Author + ", " + message.SentUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                        source: source);
                    tasks++;
                    break;
                case "факт":
                case "решение":
                    _ = KnowledgeGraphService.AddCapsuleAsync(new SmartCapsule
                    {
                        Name = (kind == "факт" ? "Факт" : "Решение") + " · " + roomName,
                        Summary = value + " — " + message.Author,
                        Titles = [message.Author + ", " + message.SentUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm")]
                    });
                    facts++;
                    break;
            }
        }
        if (tasks + facts > 0)
            CrashReportService.AddBreadcrumb("chat-graph", $"absorbed-{tasks}-{facts}");
        return (tasks, facts);
    }
}
