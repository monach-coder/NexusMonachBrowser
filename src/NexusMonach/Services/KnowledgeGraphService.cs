using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Локальная память просмотра: страницы становятся узлами, а смысловая близость,
/// переходы и общие источники — разными типами связей. Отдельный список истории
/// не требуется: найти и повторно открыть страницу можно прямо из графа.
/// </summary>
public static class KnowledgeGraphService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly SemaphoreSlim SemanticGate = new(1, 1);
    private static KnowledgeGraphData _data = new();

    public static async Task InitializeAsync()
    {
        _data = await JsonStore.ReadAsync<KnowledgeGraphData>(AppPaths.KnowledgeGraphFile) ?? new KnowledgeGraphData();
        _data.Nodes ??= [];
        _data.Edges ??= [];
        _data.ResearchSessions ??= [];
        NormalizeLoadedData();
        await ImportLegacyHistoryAsync();
    }

    public static KnowledgeGraphData Snapshot()
    {
        Gate.Wait();
        try
        {
            return new KnowledgeGraphData
            {
                Nodes = _data.Nodes.Select(Clone).ToList(),
                Edges = _data.Edges.Select(Clone).ToList(),
                ResearchSessions = _data.ResearchSessions.Select(Clone).ToList()
            };
        }
        finally { Gate.Release(); }
    }

    public static async Task IndexPageAsync(string title, string url, string text, string? previousUrl = null,
        CancellationToken cancellationToken = default)
    {
        if (!SettingsService.Current.BuildKnowledgeGraph || string.IsNullOrWhiteSpace(url) ||
            UrlService.IsInternal(url) || IsSensitivePage(url, title)) return;
        url = NormalizeUrl(url);
        previousUrl = string.IsNullOrWhiteSpace(previousUrl) ? null : NormalizeUrl(previousUrl);

        PageSemantics semantics;
        List<float> embedding;
        await SemanticGate.WaitAsync(cancellationToken);
        try
        {
            semantics = await LocalIntelligenceService.AnalyzePageAsync(title, url, text, cancellationToken);
            embedding = await NexusFabricRuntime.EmbedSemanticsAsync(
                title + "\n" + semantics.Summary + "\n" + text[..Math.Min(text.Length, 12_000)], cancellationToken);
        }
        finally { SemanticGate.Release(); }

        await Gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var node = _data.Nodes.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                node = new KnowledgeNode
                {
                    Title = title, Url = url, CreatedAtUtc = now, LastVisitedAtUtc = now,
                    RecentVisitsUtc = [now]
                };
                _data.Nodes.Add(node);
            }
            else
            {
                node.VisitCount++;
                node.LastVisitedAtUtc = now;
                node.RecentVisitsUtc ??= [];
                if (node.RecentVisitsUtc.Count == 0 || now - node.RecentVisitsUtc[^1] > TimeSpan.FromSeconds(20))
                    node.RecentVisitsUtc.Add(now);
                if (node.RecentVisitsUtc.Count > 24)
                    node.RecentVisitsUtc.RemoveRange(0, node.RecentVisitsUtc.Count - 24);
            }

            node.Title = string.IsNullOrWhiteSpace(title) ? url : title.Trim();
            node.Summary = semantics.Summary;
            node.Keywords = semantics.Keywords.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().Take(12).ToList();
            node.Embedding = embedding;
            node.Domain = GetDomain(url);
            node.PageKind = ClassifyPage(url, title, text);
            node.Topic = PickTopic(node);

            RebuildSemanticEdgesFor(node);
            if (previousUrl is not null && !previousUrl.Equals(url, StringComparison.OrdinalIgnoreCase))
                StrengthenNavigationEdge(previousUrl, node, now);
            TrimGraph();
            await JsonStore.WriteAsync(AppPaths.KnowledgeGraphFile, _data);
        }
        finally { Gate.Release(); }
    }

    public static async Task<IReadOnlyList<KnowledgeSearchHit>> SearchAsync(string query,
        CancellationToken cancellationToken = default)
    {
        var snapshot = Snapshot();
        query = query.Trim();
        if (string.IsNullOrWhiteSpace(query))
            return snapshot.Nodes.OrderByDescending(x => x.IsPinned).ThenByDescending(x => x.LastVisitedAtUtc)
                .Take(180).Select(x => new KnowledgeSearchHit { Node = x, Score = 1, MatchReason = "недавняя страница" }).ToArray();

        var queryEmbedding = await NexusFabricRuntime.EmbedSemanticsAsync(query, cancellationToken);
        var words = ExtractQueryWords(query);
        var now = DateTime.UtcNow;
        return snapshot.Nodes.Select(node =>
            {
                var searchable = (node.Title + " " + node.Url + " " + node.Summary + " " +
                                  string.Join(" ", node.Keywords)).ToLowerInvariant();
                var exact = searchable.Contains(query, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0;
                var wordMatches = words.Count == 0 ? 0 : words.Count(x => searchable.Contains(x, StringComparison.OrdinalIgnoreCase)) / (double)words.Count;
                var semantic = Cosine(queryEmbedding, node.Embedding);
                var recency = Math.Exp(-Math.Max(0, (now - node.LastVisitedAtUtc).TotalDays) / 90);
                var score = exact * 0.48 + wordMatches * 0.27 + Math.Max(0, semantic) * 0.42 + recency * 0.05 +
                            Math.Min(0.08, Math.Log2(node.VisitCount + 1) * 0.018) + (node.IsPinned ? 0.1 : 0);
                var reason = exact > 0 ? "точное совпадение" : semantic >= 0.45 ? "смысловая близость" :
                    wordMatches > 0 ? "совпали понятия" : "слабая связь";
                return new KnowledgeSearchHit { Node = node, Score = score, MatchReason = reason };
            })
            .Where(x => x.Score >= 0.12)
            .OrderByDescending(x => x.Score)
            .Take(180).ToArray();
    }

    public static async Task RecordResearchAsync(NexusSearchReport report,
        CancellationToken cancellationToken = default, string pageKind = "источник поиска")
    {
        if (!SettingsService.Current.BuildKnowledgeGraph || report.Items.Count == 0 ||
            IsSensitiveResearchQuery(report.Query)) return;
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var now = DateTime.UtcNow;
            var queryWords = ExtractQueryWords(report.Query);
            var nodeIds = new List<string>();
            foreach (var item in report.Items.Take(8))
            {
                if (!Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) || uri.Scheme is not "http" and not "https")
                    continue;
                var url = NormalizeUrl(item.Url);
                var node = _data.Nodes.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
                if (node is null)
                {
                    node = new KnowledgeNode
                    {
                        Title = item.Title, Url = url, Domain = GetDomain(url), Summary = item.Answer,
                        PageKind = pageKind, Topic = queryWords.FirstOrDefault() ?? GetDomain(url),
                        CreatedAtUtc = now, LastVisitedAtUtc = now, VisitCount = 0, RecentVisitsUtc = []
                    };
                    _data.Nodes.Add(node);
                }
                else if (string.IsNullOrWhiteSpace(node.Summary) || node.PageKind == "источник поиска")
                {
                    node.Title = string.IsNullOrWhiteSpace(item.Title) ? node.Title : item.Title;
                    node.Summary = string.IsNullOrWhiteSpace(item.Answer) ? item.Snippet : item.Answer;
                }
                node.Keywords = node.Keywords.Concat(queryWords).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToList();
                nodeIds.Add(node.Id);
            }

            if (nodeIds.Count == 0) return;
            var hub = nodeIds[0];
            foreach (var target in nodeIds.Skip(1))
            {
                var edge = _data.Edges.FirstOrDefault(x => x.Kind == "research" && x.SourceId == hub && x.TargetId == target);
                if (edge is null)
                    _data.Edges.Add(new KnowledgeEdge
                    {
                        SourceId = hub, TargetId = target, Kind = "research",
                        Relation = "поиск: " + report.Query[..Math.Min(report.Query.Length, 90)],
                        Evidence = queryWords.Take(6).ToList(), Score = .68, LastSeenAtUtc = now
                    });
                else
                {
                    edge.Strength++;
                    edge.LastSeenAtUtc = now;
                    edge.Score = Math.Min(1, edge.Score + .04);
                }
            }
            var sourceDomain = _data.Nodes.FirstOrDefault(x => x.Id == nodeIds[0])?.Domain ?? string.Empty;
            var sessionKind = pageKind == "товар" ? "shopping" :
                pageKind == "исследование выбранного сайта" ? "site-research" : "search";
            var session = _data.ResearchSessions.LastOrDefault(x =>
                x.Query.Equals(report.Query, StringComparison.OrdinalIgnoreCase) &&
                x.Kind.Equals(sessionKind, StringComparison.OrdinalIgnoreCase) &&
                IsSameResearchDomain(x.SourceDomain, sourceDomain) &&
                now - x.UpdatedAtUtc < TimeSpan.FromHours(2));
            if (session is null)
            {
                session = new KnowledgeResearchSession
                {
                    Query = report.Query, CreatedAtUtc = now, UpdatedAtUtc = now,
                    Kind = sessionKind, SourceDomain = sourceDomain
                };
                _data.ResearchSessions.Add(session);
            }
            session.UpdatedAtUtc = now;
            session.ResultNodeIds = session.ResultNodeIds.Concat(nodeIds).Distinct().Take(40).ToList();
            if (_data.ResearchSessions.Count > 300)
                _data.ResearchSessions.RemoveRange(0, _data.ResearchSessions.Count - 300);
            DeduplicateEdges();
            TrimGraph();
            await JsonStore.WriteAsync(AppPaths.KnowledgeGraphFile, _data);
        }
        finally { Gate.Release(); }
    }

    public static Task RecordShoppingResearchAsync(ShoppingReport shopping,
        CancellationToken cancellationToken = default)
    {
        var report = new NexusSearchReport(shopping.Query, shopping.Recommendation,
            shopping.Items.Where(x => !string.IsNullOrWhiteSpace(x.Url)).Select(x =>
                new NexusSearchItem(x.Name, x.Url,
                    $"Цена: {x.Price}; рейтинг: {x.Rating}; купили/отзывы: {x.Buyers}",
                    x.Strengths, x.Score)).ToArray(),
            "Карточки извлечены Nexus Следопытом из DOM каталога и обработаны локально.");
        return RecordResearchAsync(report, cancellationToken, "товар");
    }

    public static async Task RecordResearchChoiceAsync(string query, string url)
    {
        if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(url)) return;
        await Gate.WaitAsync();
        try
        {
            var node = _data.Nodes.FirstOrDefault(x => x.Url.Equals(NormalizeUrl(url), StringComparison.OrdinalIgnoreCase));
            var session = _data.ResearchSessions.LastOrDefault(x =>
                x.Query.Equals(query, StringComparison.OrdinalIgnoreCase) &&
                (node is null || x.ResultNodeIds.Contains(node.Id)));
            if (session is null || node is null) return;
            session.SelectedNodeId = node.Id;
            await JsonStore.WriteAsync(AppPaths.KnowledgeGraphFile, _data);
        }
        finally { Gate.Release(); }
    }

    public static async Task SetPinnedAsync(string nodeId, bool pinned)
    {
        await Gate.WaitAsync();
        try
        {
            var node = _data.Nodes.FirstOrDefault(x => x.Id == nodeId);
            if (node is null) return;
            node.IsPinned = pinned;
            await JsonStore.WriteAsync(AppPaths.KnowledgeGraphFile, _data);
        }
        finally { Gate.Release(); }
    }

    public static async Task AddCapsuleAsync(SmartCapsule capsule)
    {
        string? previous = null;
        for (var i = 0; i < capsule.Urls.Count; i++)
        {
            var title = i < capsule.Titles.Count ? capsule.Titles[i] : capsule.Urls[i];
            await IndexPageAsync(title, capsule.Urls[i], capsule.Name + ". " + capsule.Summary, previous);
            previous = capsule.Urls[i];
        }
    }

    public static async Task ClearAsync()
    {
        await Gate.WaitAsync();
        try
        {
            _data = new KnowledgeGraphData();
            await JsonStore.WriteAsync(AppPaths.KnowledgeGraphFile, _data);
        }
        finally { Gate.Release(); }
    }

    private static void RebuildSemanticEdgesFor(KnowledgeNode node)
    {
        _data.Edges.RemoveAll(x => (x.Kind is "semantic" or "domain") &&
                                   (x.SourceId == node.Id || x.TargetId == node.Id));
        var own = node.Keywords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(KnowledgeNode Node, double Score, string[] Common, bool SameDomain)>();
        foreach (var other in _data.Nodes.Where(x => x.Id != node.Id))
        {
            var theirs = other.Keywords.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var common = own.Intersect(theirs, StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
            var union = own.Union(theirs, StringComparer.OrdinalIgnoreCase).Count();
            var keywordScore = union == 0 ? 0 : common.Length / (double)union;
            var semantic = Cosine(node.Embedding, other.Embedding);
            var sameDomain = !string.IsNullOrWhiteSpace(node.Domain) && node.Domain.Equals(other.Domain, StringComparison.OrdinalIgnoreCase);
            var score = Math.Max(semantic, keywordScore * 1.25) + (sameDomain ? 0.06 : 0);
            if (score < 0.24 && common.Length < 2) continue;
            candidates.Add((other, Math.Clamp(score, 0, 1), common, sameDomain));
        }

        foreach (var candidate in candidates.OrderByDescending(x => x.Score).Take(12))
        {
            var ids = new[] { node.Id, candidate.Node.Id }.OrderBy(x => x, StringComparer.Ordinal).ToArray();
            _data.Edges.Add(new KnowledgeEdge
            {
                SourceId = ids[0], TargetId = ids[1], Kind = candidate.SameDomain ? "domain" : "semantic",
                Relation = candidate.Common.Length > 0 ? string.Join(", ", candidate.Common.Take(3)) :
                    candidate.SameDomain ? "общий источник" : "смысловая близость",
                Evidence = candidate.Common.ToList(), Score = candidate.Score,
                LastSeenAtUtc = DateTime.UtcNow
            });
        }
        DeduplicateEdges();
    }

    private static void StrengthenNavigationEdge(string previousUrl, KnowledgeNode target, DateTime now)
    {
        var source = _data.Nodes.FirstOrDefault(x => x.Url.Equals(previousUrl, StringComparison.OrdinalIgnoreCase));
        if (source is null || source.Id == target.Id) return;
        var edge = _data.Edges.FirstOrDefault(x => x.Kind == "navigation" &&
            x.SourceId == source.Id && x.TargetId == target.Id);
        if (edge is null)
        {
            _data.Edges.Add(new KnowledgeEdge
            {
                SourceId = source.Id, TargetId = target.Id, Kind = "navigation", Relation = "переход",
                Score = 0.72, Strength = 1, LastSeenAtUtc = now
            });
        }
        else
        {
            edge.Strength++;
            edge.Score = Math.Min(1, 0.68 + Math.Log2(edge.Strength + 1) * 0.09);
            edge.LastSeenAtUtc = now;
        }
    }

    private static void DeduplicateEdges()
    {
        _data.Edges = _data.Edges.GroupBy(x => (x.SourceId, x.TargetId, x.Kind))
            .Select(group => group.OrderByDescending(x => x.Score).First()).ToList();
    }

    private static void TrimGraph()
    {
        if (_data.Nodes.Count <= 1800) return;
        var remove = _data.Nodes.Where(x => !x.IsPinned).OrderBy(x => x.LastVisitedAtUtc)
            .Take(_data.Nodes.Count - 1800).Select(x => x.Id).ToHashSet();
        _data.Nodes.RemoveAll(x => remove.Contains(x.Id));
        _data.Edges.RemoveAll(x => remove.Contains(x.SourceId) || remove.Contains(x.TargetId));
        foreach (var session in _data.ResearchSessions)
            session.ResultNodeIds.RemoveAll(remove.Contains);
        _data.ResearchSessions.RemoveAll(x => x.ResultNodeIds.Count == 0);
    }

    private static async Task ImportLegacyHistoryAsync()
    {
        if (!File.Exists(AppPaths.HistoryFile)) return;
        var legacy = await JsonStore.ReadAsync<List<HistoryEntry>>(AppPaths.HistoryFile) ?? [];
        foreach (var item in legacy.OrderBy(x => x.VisitedAtUtc))
        {
            if (string.IsNullOrWhiteSpace(item.Url) || UrlService.IsInternal(item.Url) || IsSensitivePage(item.Url, item.Title)) continue;
            var url = NormalizeUrl(item.Url);
            var node = _data.Nodes.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                node = new KnowledgeNode
                {
                    Title = string.IsNullOrWhiteSpace(item.Title) ? url : item.Title, Url = url,
                    Domain = GetDomain(url), CreatedAtUtc = item.VisitedAtUtc, LastVisitedAtUtc = item.VisitedAtUtc,
                    RecentVisitsUtc = [item.VisitedAtUtc], PageKind = ClassifyPage(url, item.Title, string.Empty)
                };
                _data.Nodes.Add(node);
            }
            else
            {
                node.VisitCount++;
                node.LastVisitedAtUtc = item.VisitedAtUtc > node.LastVisitedAtUtc ? item.VisitedAtUtc : node.LastVisitedAtUtc;
                node.RecentVisitsUtc.Add(item.VisitedAtUtc);
                node.RecentVisitsUtc = node.RecentVisitsUtc.OrderBy(x => x).TakeLast(24).ToList();
            }
        }
        await JsonStore.WriteAsync(AppPaths.KnowledgeGraphFile, _data);
        try { File.Delete(AppPaths.HistoryFile); } catch { }
    }

    private static void NormalizeLoadedData()
    {
        _data.ResearchSessions ??= [];
        foreach (var session in _data.ResearchSessions)
        {
            session.ResultNodeIds ??= [];
            if (session.UpdatedAtUtc == default) session.UpdatedAtUtc = session.CreatedAtUtc;
            session.Kind = string.IsNullOrWhiteSpace(session.Kind) ? "research" : session.Kind;
            session.SourceDomain ??= string.Empty;
        }
        foreach (var node in _data.Nodes)
        {
            node.Keywords ??= [];
            node.Embedding ??= [];
            node.RecentVisitsUtc ??= [];
            if (node.RecentVisitsUtc.Count == 0 && node.VisitCount > 0) node.RecentVisitsUtc.Add(node.LastVisitedAtUtc);
            node.Domain = string.IsNullOrWhiteSpace(node.Domain) ? GetDomain(node.Url) : node.Domain;
            node.PageKind = string.IsNullOrWhiteSpace(node.PageKind) ? ClassifyPage(node.Url, node.Title, string.Empty) : node.PageKind;
            node.Topic = string.IsNullOrWhiteSpace(node.Topic) ? PickTopic(node) : node.Topic;
        }
        foreach (var edge in _data.Edges)
        {
            edge.Kind = string.IsNullOrWhiteSpace(edge.Kind) ? "semantic" : edge.Kind;
            edge.Evidence ??= [];
            if (edge.Strength < 1) edge.Strength = 1;
        }
        DeduplicateEdges();
    }

    private static KnowledgeNode Clone(KnowledgeNode x) => new()
    {
        Id = x.Id, Title = x.Title, Url = x.Url, Summary = x.Summary, Domain = x.Domain,
        PageKind = x.PageKind, Topic = x.Topic, Keywords = x.Keywords.ToList(), Embedding = x.Embedding.ToList(),
        CreatedAtUtc = x.CreatedAtUtc, LastVisitedAtUtc = x.LastVisitedAtUtc, VisitCount = x.VisitCount,
        RecentVisitsUtc = x.RecentVisitsUtc.ToList(), IsPinned = x.IsPinned
    };

    private static KnowledgeEdge Clone(KnowledgeEdge x) => new()
    {
        SourceId = x.SourceId, TargetId = x.TargetId, Relation = x.Relation, Kind = x.Kind,
        Evidence = x.Evidence.ToList(), Score = x.Score, Strength = x.Strength, LastSeenAtUtc = x.LastSeenAtUtc
    };

    private static KnowledgeResearchSession Clone(KnowledgeResearchSession x) => new()
    {
        Id = x.Id, Query = x.Query, CreatedAtUtc = x.CreatedAtUtc, UpdatedAtUtc = x.UpdatedAtUtc,
        Kind = x.Kind, SourceDomain = x.SourceDomain,
        ResultNodeIds = x.ResultNodeIds.ToList(), SelectedNodeId = x.SelectedNodeId
    };

    private static bool IsSensitiveResearchQuery(string query) =>
        System.Text.RegularExpressions.Regex.IsMatch(query,
            @"(?:[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}|\b\d{7,}\b|парол|password|одноразов\p{L}*\s+код|otp)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static bool IsSameResearchDomain(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        (left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
         left.EndsWith('.' + right, StringComparison.OrdinalIgnoreCase) ||
         right.EndsWith('.' + left, StringComparison.OrdinalIgnoreCase));

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count) return 0;
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i]; a += left[i] * left[i]; b += right[i] * right[i];
        }
        return a <= 0 || b <= 0 ? 0 : dot / Math.Sqrt(a * b);
    }

    private static List<string> ExtractQueryWords(string query) =>
        System.Text.RegularExpressions.Regex.Matches(query.ToLowerInvariant(), @"[\p{L}\p{N}]{3,}")
            .Select(x => x.Value).Distinct().Take(12).ToList();

    private static string PickTopic(KnowledgeNode node) =>
        node.Keywords.FirstOrDefault(x => x.Length >= 3) ?? (!string.IsNullOrWhiteSpace(node.Domain) ? node.Domain : "прочее");

    private static string GetDomain(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase) : string.Empty;

    private static string ClassifyPage(string url, string title, string text)
    {
        var value = (url + " " + title + " " + text[..Math.Min(text.Length, 400)]).ToLowerInvariant();
        if (value.Contains("/watch") || value.Contains("video") || value.Contains("youtube")) return "видео";
        if (value.Contains("product") || value.Contains("товар") || value.Contains("/item/")) return "товар";
        if (value.Contains("docs") || value.Contains("documentation") || value.Contains("справк")) return "документация";
        if (value.Contains("news") || value.Contains("статья") || value.Contains("article")) return "статья";
        if (value.Contains("search") || value.Contains("поиск") || value.Contains("query=")) return "поиск";
        return "страница";
    }

    private static bool IsSensitivePage(string url, string title)
    {
        var value = (url + " " + title).ToLowerInvariant();
        string[] markers = ["/login", "/signin", "/auth", "/account", "/checkout", "/payment",
            "banking", "webmail", "личный кабинет", "оплата", "оформление заказа"];
        return markers.Any(marker => value.Contains(marker, StringComparison.Ordinal));
    }

    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        var cleaned = UrlService.CleanTrackingParameters(value);
        if (!Uri.TryCreate(cleaned, UriKind.Absolute, out uri)) return value;
        var builder = new UriBuilder(uri) { Fragment = string.Empty };
        return builder.Uri.AbsoluteUri;
    }
}
