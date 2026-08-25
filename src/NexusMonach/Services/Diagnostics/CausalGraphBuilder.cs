namespace NexusMonach.Services.Diagnostics;

/// <summary>Узел причинного графа: событие, подсистема или исключение.</summary>
public sealed record CausalNode(
    string Id,
    string Kind,
    string Title,
    DateTimeOffset TimestampUtc,
    string? Details = null);

/// <summary>Ребро графа: из FromId в ToId с типом связи и задержкой в мс.</summary>
public sealed record CausalEdge(string FromId, string ToId, string Relation, int LagMs);

/// <summary>
/// Причинный граф отказа: хронология breadcrumbs, системные события Windows
/// и само исключение, связанные рёбрами «предшествовало/вызвало». Корень —
/// самая ранняя причина, из которой достижим узел отказа.
/// </summary>
public sealed record CausalGraph(
    IReadOnlyList<CausalNode> Nodes,
    IReadOnlyList<CausalEdge> Edges,
    string RootCauseNodeId,
    string Summary);

/// <summary>Системное событие Windows, попавшее в окно корреляции.</summary>
public sealed record SystemEventRecord(
    string Kind,
    string Title,
    DateTimeOffset TimestampUtc,
    string Details);

/// <summary>
/// Строит причинный граф по контексту сбоя. Чистая функция без обращений к ОС —
/// источники данных передаются снаружи, вся логика покрывается юнит-тестами.
/// </summary>
public static class CausalGraphBuilder
{
    public const string RelationCaused = "вызвало";
    public const string RelationPreceded = "предшествовало";
    public const string RelationCorrelated = "скоррелировано";

    /// <summary>
    /// Входные данные для построения графа: исключение с контекстом,
    /// накопленные breadcrumbs и системные события за окно корреляции.
    /// </summary>
    public sealed record CrashContext(
        string ExceptionType,
        string ExceptionMessage,
        string Component,
        string Stage,
        IReadOnlyList<(DateTimeOffset TimestampUtc, string Component, string Stage)> Breadcrumbs,
        IReadOnlyList<SystemEventRecord> SystemEvents);

    public static CausalGraph Build(CrashContext context)
    {
        var nodes = new List<CausalNode>();
        var edges = new List<CausalEdge>();

        // Хронология breadcrumbs — скелет графа.
        string? previousCrumbId = null;
        DateTimeOffset? previousCrumbTime = null;
        foreach (var crumb in context.Breadcrumbs.OrderBy(b => b.TimestampUtc))
        {
            var id = $"b{nodes.Count}";
            nodes.Add(new CausalNode(id, "event", $"{crumb.Component} · {crumb.Stage}", crumb.TimestampUtc));
            if (previousCrumbId is not null)
                edges.Add(new CausalEdge(previousCrumbId, id, RelationPreceded,
                    Lag(previousCrumbTime, crumb.TimestampUtc)));
            previousCrumbId = id;
            previousCrumbTime = crumb.TimestampUtc;
        }

        // Сам сбой — целевой узел графа. Заголовок самодостаточен: тип и
        // первая строка сообщения видны в любом визуализаторе без деталей.
        var crashId = "crash";
        var crashTime = DateTimeOffset.UtcNow;
        var shortMessage = context.ExceptionMessage.Split('\n')[0];
        if (shortMessage.Length > 80) shortMessage = shortMessage[..80] + "…";
        nodes.Add(new CausalNode(crashId, "exception",
            $"{context.Component}/{context.Stage}: {context.ExceptionType} — {shortMessage}", crashTime,
            context.ExceptionMessage));
        if (previousCrumbId is not null)
            edges.Add(new CausalEdge(previousCrumbId, crashId, RelationPreceded,
                Lag(previousCrumbTime, crashTime)));

        // Системные события Windows: краши процессов, сбои драйвера графики.
        var renderFailure = IsRenderThreadFailure(context.ExceptionType, context.ExceptionMessage);
        var compositionDisabled = IsCompositionDisabled(context.ExceptionType, context.ExceptionMessage);
        foreach (var systemEvent in context.SystemEvents.OrderBy(e => e.TimestampUtc))
        {
            var id = $"s{nodes.Count}";
            nodes.Add(new CausalNode(id, "system", systemEvent.Title, systemEvent.TimestampUtc,
                systemEvent.Details));

            // Краш графической подсистемы или зависание драйвера — известные
            // причины отказов рендеринга; свяжем их с отказом напрямую.
            var isDwmAppCrash = systemEvent.Kind == SystemEventReader.KindAppCrash &&
                                systemEvent.Title.Contains("dwm", StringComparison.OrdinalIgnoreCase);
            var isGraphicsCause = systemEvent.Kind
                                      is SystemEventReader.KindDisplayDriverReset
                                      or SystemEventReader.KindDwmCrash || isDwmAppCrash;
            if ((renderFailure || compositionDisabled) && isGraphicsCause)
                edges.Add(new CausalEdge(id, crashId, RelationCaused,
                    Lag(systemEvent.TimestampUtc, crashTime)));
            else
                edges.Add(new CausalEdge(id, crashId, RelationCorrelated,
                    Lag(systemEvent.TimestampUtc, crashTime)));
        }

        // Зависание рендерера WebView2 — известный предвестник гибели потока
        // отрисовки WPF (наблюдалось в рапорте от 24.08.2026).
        if (renderFailure)
        {
            var unresponsive = nodes.FirstOrDefault(n =>
                n.Title.Contains("RenderProcessUnresponsive", StringComparison.OrdinalIgnoreCase));
            if (unresponsive is not null)
                edges.Add(new CausalEdge(unresponsive.Id, crashId, RelationCaused,
                    Lag(unresponsive.TimestampUtc, crashTime)));
        }

        var root = FindRootCause(nodes, edges, crashId);
        return new CausalGraph(nodes, edges, root?.Id ?? crashId, BuildSummary(root, nodes.Find(n => n.Id == crashId)!));
    }

    /// <summary>
    /// Корневая причина: самый ранний узел, из которого достижим отказ
    /// по рёбрам «вызвало»; если прямых причин нет — самое раннее событие.
    /// </summary>
    private static CausalNode? FindRootCause(
        IReadOnlyList<CausalNode> nodes, IReadOnlyList<CausalEdge> edges, string crashId)
    {
        var incoming = edges.Where(e => e.ToId == crashId && e.Relation == RelationCaused).ToList();
        if (incoming.Count == 0)
            incoming = edges.Where(e => e.ToId == crashId).ToList();
        if (incoming.Count == 0)
            return nodes.OrderBy(n => n.TimestampUtc).FirstOrDefault(n => n.Id != crashId);

        var candidates = incoming
            .Select(e => nodes.FirstOrDefault(n => n.Id == e.FromId))
            .Where(n => n is not null)
            .Cast<CausalNode>()
            .ToList();
        // Поднимаемся по цепочке «вызвало» до самой ранней причины.
        var seen = new HashSet<string>();
        while (true)
        {
            var deeper = candidates
                .SelectMany(c => edges.Where(e => e.ToId == c.Id && e.Relation == RelationCaused)
                    .Select(e => nodes.FirstOrDefault(n => n.Id == e.FromId))
                    .Where(n => n is not null && seen.Add(n!.Id)))
                .Cast<CausalNode>()
                .ToList();
            if (deeper.Count == 0) break;
            candidates.AddRange(deeper);
        }
        return candidates.OrderBy(n => n.TimestampUtc).First();
    }

    private static string BuildSummary(CausalNode? root, CausalNode crash)
    {
        if (root is null || root.Id == crash.Id)
            return $"Отказ: {crash.Title}. Прямых внешних причин не найдено.";
        return $"Причина: {root.Title} → отказ: {crash.Title}.";
    }

    internal static bool IsRenderThreadFailure(string exceptionType, string message) =>
        exceptionType.EndsWith("COMException", StringComparison.Ordinal) &&
            message.Contains("UCEERR_RENDERTHREADFAILURE", StringComparison.Ordinal) ||
            message.Contains("0x88980406", StringComparison.Ordinal);

    internal static bool IsCompositionDisabled(string exceptionType, string message) =>
        exceptionType.EndsWith("COMException", StringComparison.Ordinal) &&
            message.Contains("Композиция рабочего стола отключена", StringComparison.Ordinal) ||
            message.Contains("0x80263001", StringComparison.Ordinal);

    private static int Lag(DateTimeOffset? from, DateTimeOffset to) =>
        from is { } start ? Math.Max(0, (int)(to - start).TotalMilliseconds) : 0;
}
