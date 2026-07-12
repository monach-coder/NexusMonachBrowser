using NexusMonach.Models;

namespace NexusMonach.Services;

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
    }

    public static KnowledgeGraphData Snapshot()
    {
        Gate.Wait();
        try
        {
            return new KnowledgeGraphData
            {
                Nodes = _data.Nodes.Select(Clone).ToList(),
                Edges = _data.Edges.Select(x => new KnowledgeEdge
                {
                    SourceId = x.SourceId, TargetId = x.TargetId, Relation = x.Relation, Score = x.Score
                }).ToList()
            };
        }
        finally { Gate.Release(); }
    }

    public static async Task IndexPageAsync(string title, string url, string text,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url) || UrlService.IsInternal(url) || IsSensitivePage(url, title)) return;
        url = NormalizeUrl(url);
        PageSemantics semantics;
        List<float> embedding;
        await SemanticGate.WaitAsync(cancellationToken);
        try
        {
            semantics = await LocalIntelligenceService.AnalyzePageAsync(title, url, text, cancellationToken);
            embedding = await SemanticEmbeddingService.EmbedAsync(title + "\n" + semantics.Summary + "\n" + text,
                cancellationToken);
        }
        finally { SemanticGate.Release(); }
        await Gate.WaitAsync(cancellationToken);
        try
        {
            var node = _data.Nodes.FirstOrDefault(x => x.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (node is null)
            {
                node = new KnowledgeNode { Title = title, Url = url };
                _data.Nodes.Add(node);
            }
            else
            {
                node.VisitCount++;
                node.LastVisitedAtUtc = DateTime.UtcNow;
            }
            node.Title = string.IsNullOrWhiteSpace(title) ? url : title;
            node.Summary = semantics.Summary;
            node.Keywords = semantics.Keywords.ToList();
            node.Embedding = embedding;
            RebuildEdgesFor(node);
            if (_data.Nodes.Count > 1500)
            {
                var remove = _data.Nodes.OrderBy(x => x.LastVisitedAtUtc).Take(_data.Nodes.Count - 1500)
                    .Select(x => x.Id).ToHashSet();
                _data.Nodes.RemoveAll(x => remove.Contains(x.Id));
                _data.Edges.RemoveAll(x => remove.Contains(x.SourceId) || remove.Contains(x.TargetId));
            }
            await JsonStore.WriteAsync(AppPaths.KnowledgeGraphFile, _data);
        }
        finally { Gate.Release(); }
    }

    public static async Task AddCapsuleAsync(SmartCapsule capsule)
    {
        for (var i = 0; i < capsule.Urls.Count; i++)
        {
            var title = i < capsule.Titles.Count ? capsule.Titles[i] : capsule.Urls[i];
            await IndexPageAsync(title, capsule.Urls[i], capsule.Name + ". " + capsule.Summary);
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

    private static void RebuildEdgesFor(KnowledgeNode node)
    {
        _data.Edges.RemoveAll(x => x.SourceId == node.Id || x.TargetId == node.Id);
        var own = node.Keywords.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var other in _data.Nodes.Where(x => x.Id != node.Id))
        {
            var theirs = other.Keywords.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var union = own.Union(theirs, StringComparer.OrdinalIgnoreCase).Count();
            if (union == 0) continue;
            var common = own.Intersect(theirs, StringComparer.OrdinalIgnoreCase).ToArray();
            var score = Cosine(node.Embedding, other.Embedding);
            if (score <= 0) score = common.Length / (double)union;
            if (score < 0.18) continue;
            _data.Edges.Add(new KnowledgeEdge
            {
                SourceId = node.Id, TargetId = other.Id,
                Relation = string.Join(", ", common.Take(3)), Score = score
            });
        }
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        if (left.Count == 0 || left.Count != right.Count) return 0;
        double dot = 0, a = 0, b = 0;
        for (var i = 0; i < left.Count; i++)
        {
            dot += left[i] * right[i];
            a += left[i] * left[i];
            b += right[i] * right[i];
        }
        return a <= 0 || b <= 0 ? 0 : dot / Math.Sqrt(a * b);
    }

    private static KnowledgeNode Clone(KnowledgeNode x) => new()
    {
        Id = x.Id, Title = x.Title, Url = x.Url, Summary = x.Summary,
        Keywords = x.Keywords.ToList(), Embedding = x.Embedding.ToList(), CreatedAtUtc = x.CreatedAtUtc,
        LastVisitedAtUtc = x.LastVisitedAtUtc, VisitCount = x.VisitCount
    };

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
        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.AbsoluteUri;
    }
}
