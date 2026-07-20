namespace NexusMonach.Models;

public sealed class KnowledgeNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string PageKind { get; set; } = "страница";
    public string Topic { get; set; } = "прочее";
    public List<string> Keywords { get; set; } = [];
    public List<float> Embedding { get; set; } = [];
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastVisitedAtUtc { get; set; } = DateTime.UtcNow;
    public int VisitCount { get; set; } = 1;
    public List<DateTime> RecentVisitsUtc { get; set; } = [DateTime.UtcNow];
    public bool IsPinned { get; set; }
}

public sealed class KnowledgeEdge
{
    public string SourceId { get; set; } = string.Empty;
    public string TargetId { get; set; } = string.Empty;
    public string Relation { get; set; } = "общая тема";
    public string Kind { get; set; } = "semantic";
    public List<string> Evidence { get; set; } = [];
    public double Score { get; set; }
    public int Strength { get; set; } = 1;
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class KnowledgeGraphData
{
    public List<KnowledgeNode> Nodes { get; set; } = [];
    public List<KnowledgeEdge> Edges { get; set; } = [];
    public List<KnowledgeResearchSession> ResearchSessions { get; set; } = [];
}

public sealed class KnowledgeResearchSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Query { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public string Kind { get; set; } = "research";
    public string SourceDomain { get; set; } = string.Empty;
    public List<string> ResultNodeIds { get; set; } = [];
    public string? SelectedNodeId { get; set; }
}

public sealed class KnowledgeSearchHit
{
    public KnowledgeNode Node { get; set; } = new();
    public double Score { get; set; }
    public string MatchReason { get; set; } = string.Empty;
}

public sealed class SmartCapsule
{
    public string Name { get; set; } = "Тема";
    public string Summary { get; set; } = string.Empty;
    public List<string> Urls { get; set; } = [];
    public List<string> Titles { get; set; } = [];
    public string ItemsText => string.Join("\n", Titles.Select((title, index) => $"{index + 1}. {title}"));
    public string CountText => $"{Urls.Count} вкладок";
}

public sealed class AgentPlan
{
    public string Goal { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public List<AgentStep> Steps { get; set; } = [];
}

public sealed class AgentStep
{
    public string Action { get; set; } = string.Empty;
    public string ElementId { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
}

public sealed class DeveloperAnalysis
{
    public string Summary { get; set; } = string.Empty;
    public List<string> Suggestions { get; set; } = [];
    public List<DeveloperHighlight> Highlights { get; set; } = [];
}

public sealed class DeveloperHighlight
{
    public string Selector { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}

public sealed class ShoppingReport
{
    public string Query { get; set; } = string.Empty;
    public List<ShoppingCandidate> Items { get; set; } = [];
    public string Recommendation { get; set; } = string.Empty;
    public string Caveat { get; set; } = string.Empty;
}

public sealed class ShoppingCandidate
{
    public string Name { get; set; } = string.Empty;
    public string Price { get; set; } = "нет данных";
    public string Rating { get; set; } = "нет данных";
    public string Buyers { get; set; } = "нет данных";
    public string Url { get; set; } = string.Empty;
    public string ImageUrl { get; set; } = string.Empty;
    public string Strengths { get; set; } = string.Empty;
    public string Weaknesses { get; set; } = string.Empty;
    public double Score { get; set; }
}

public class TranslationSegment
{
    public string Id { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
}

public sealed class TranslationResponse
{
    public List<TranslationSegment> Items { get; set; } = [];
}

public sealed class AudioCaptureResult
{
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public string WavBase64 { get; set; } = string.Empty;
}
