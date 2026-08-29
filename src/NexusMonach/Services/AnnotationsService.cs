using System.Text.Json;

namespace NexusMonach.Services;

public enum AnnotationKind { Highlight, Note, VideoFragment }

public enum HighlightColor { Yellow, Green, Red, Blue }

/// <summary>
/// Одна запись исследователя: подсветка текста, заметка к выделению или
/// фрагмент видео — всегда с контекстом страницы (URL, заголовок, время).
/// </summary>
public sealed record PageAnnotation
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required AnnotationKind Kind { get; init; }
    public HighlightColor Color { get; init; } = HighlightColor.Yellow;
    /// <summary>Цитата выделенного текста (для подсветок и заметок).</summary>
    public string Quote { get; init; } = string.Empty;
    /// <summary>Текст заметки; для видео — подпись к фрагменту.</summary>
    public string Note { get; set; } = string.Empty;
    /// <summary>Файл носителя (webm) относительно Data, для видео-фрагментов.</summary>
    public string MediaPath { get; init; } = string.Empty;
    /// <summary>Позиция воспроизведения в момент захвата, секунды.</summary>
    public double VideoPositionSeconds { get; init; }
    /// <summary>Длительность захваченного фрагмента, секунды.</summary>
    public double DurationSeconds { get; init; }
    public required string Url { get; init; }
    public string PageTitle { get; init; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Хранилище пометок исследователя: подсветки, заметки и видео-фрагменты
/// страниц. Всё локально (Data/annotations.json + notes-media), экспорт —
/// Markdown-деревом с сохранением структуры и стилей.
/// </summary>
public static class AnnotationsService
{
    private static readonly object Gate = new();
    private static List<PageAnnotation> _annotations = [];
    private static bool _loaded;

    public static IReadOnlyList<PageAnnotation> All
    {
        get { EnsureLoaded(); return _annotations; }
    }

    public static event Action? Changed;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            try
            {
                if (File.Exists(StorePath))
                    _annotations = JsonSerializer.Deserialize<List<PageAnnotation>>(
                        File.ReadAllText(StorePath)) ?? [];
            }
            catch (Exception ex)
            {
                CrashReportService.RecordNonFatal("annotations", "load", ex);
                _annotations = [];
            }
            _loaded = true;
        }
    }

    private static string StorePath => Path.Combine(AppPaths.AppRoot, "annotations.json");
    public static string MediaDirectory => Path.Combine(AppPaths.AppRoot, "notes-media");

    public static void Add(PageAnnotation annotation)
    {
        EnsureLoaded();
        lock (Gate)
        {
            _annotations.Add(annotation);
            Save();
        }
        Changed?.Invoke();
    }

    public static void UpdateNote(Guid id, string note)
    {
        EnsureLoaded();
        lock (Gate)
        {
            var found = _annotations.FirstOrDefault(a => a.Id == id);
            if (found is null) return;
            found.Note = note;
            Save();
        }
        Changed?.Invoke();
    }

    public static void Remove(Guid id)
    {
        EnsureLoaded();
        lock (Gate)
        {
            var found = _annotations.FirstOrDefault(a => a.Id == id);
            if (found is null) return;
            _annotations.Remove(found);
            if (found.Kind == AnnotationKind.VideoFragment && found.MediaPath.Length > 0)
            {
                try
                {
                    var full = Path.Combine(AppPaths.AppRoot, found.MediaPath);
                    if (File.Exists(full)) File.Delete(full);
                }
                catch { /* носитель уже удалён — не критично */ }
            }
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Пометки для конкретного URL (для подсветки при загрузке).</summary>
    public static IReadOnlyList<PageAnnotation> ForUrl(string url)
    {
        EnsureLoaded();
        return _annotations.Where(a => a.Url == url).ToList();
    }

    private static void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
        File.WriteAllText(StorePath,
            JsonSerializer.Serialize(_annotations, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Имя цвета по-русски — для подписей в экспорте и интерфейсе.</summary>
    public static string ColorName(HighlightColor color) => color switch
    {
        HighlightColor.Yellow => "жёлтый",
        HighlightColor.Green => "зелёный",
        HighlightColor.Red => "красный",
        HighlightColor.Blue => "синий",
        _ => color.ToString()
    };

    /// <summary>
    /// Строит Markdown-документ из пометок: дерево по сайтам → страницам →
    /// записям, с сохранением структуры (заголовки, цитаты, заметки,
    /// ссылки на носители). Итог открывается любым структурированным
    /// редактором.
    /// </summary>
    public static string BuildMarkdown(IEnumerable<PageAnnotation> annotations)
    {
        var builder = new System.Text.StringBuilder();
        builder.AppendLine("# Заметки Nexus Monach");
        builder.AppendLine();
        builder.AppendLine("_Собрано: " + DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm") + "_");
        builder.AppendLine();

        foreach (var siteGroup in annotations
                     .GroupBy(a => new Uri(a.Url).Host, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            builder.AppendLine("## " + siteGroup.Key);
            builder.AppendLine();
            foreach (var pageGroup in siteGroup
                         .GroupBy(a => a.Url, StringComparer.Ordinal)
                         .OrderByDescending(g => g.Max(x => x.CreatedUtc)))
            {
                var title = pageGroup.MaxBy(a => a.PageTitle.Length)?.PageTitle;
                if (string.IsNullOrWhiteSpace(title)) title = pageGroup.Key;
                builder.AppendLine("### [" + title + "](" + pageGroup.Key + ")");
                builder.AppendLine();
                foreach (var item in pageGroup.OrderByDescending(a => a.CreatedUtc))
                {
                    AppendItem(builder, item);
                }
            }
        }
        return builder.ToString();
    }

    private static void AppendItem(System.Text.StringBuilder builder, PageAnnotation item)
    {
        var stamp = item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
        switch (item.Kind)
        {
            case AnnotationKind.Highlight:
                builder.AppendLine("> " + item.Quote.Replace("\n", "\n> "));
                builder.AppendLine();
                builder.AppendLine("*выделение: " + ColorName(item.Color) + " · " + stamp + "*");
                if (item.Note.Length > 0)
                    builder.AppendLine("> 📝 " + item.Note.Replace("\n", "\n> "));
                builder.AppendLine();
                break;
            case AnnotationKind.Note:
                builder.AppendLine("> " + item.Quote.Replace("\n", "\n> "));
                builder.AppendLine();
                builder.AppendLine("📝 **" + item.Note.Replace("\n", " ") + "**");
                builder.AppendLine();
                builder.AppendLine("*" + stamp + "*");
                builder.AppendLine();
                break;
            case AnnotationKind.VideoFragment:
                builder.AppendLine("🎬 **Видео-фрагмент** — " +
                    TimeSpan.FromSeconds(item.VideoPositionSeconds).ToString(@"mm\:ss") + " (+" +
                    TimeSpan.FromSeconds(item.DurationSeconds).ToString(@"mm\:ss") + ") · " + stamp);
                if (item.MediaPath.Length > 0)
                    builder.AppendLine("   [носитель](./notes-media/" +
                        Path.GetFileName(item.MediaPath).Replace(" ", "%20") + ")");
                if (item.Note.Length > 0)
                    builder.AppendLine("   📝 " + item.Note.Replace("\n", " "));
                builder.AppendLine();
                break;
        }
    }
}
