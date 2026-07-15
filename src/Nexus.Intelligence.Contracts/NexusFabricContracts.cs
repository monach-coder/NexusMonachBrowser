// SPDX-License-Identifier: MIT

using System.Text.Json;

namespace NexusMonach.Intelligence;

/// <summary>
/// Возможности закрытого модуля перечислены в публичном контракте, чтобы браузер
/// и сторонние расширения могли корректно определять их наличие без доступа к реализации.
/// </summary>
public enum NexusFabricFeature
{
    SmartOmnibox,
    DeepPageQuestionAnswering,
    DeepPageAnalysis,
    InformationValueRadar,
    DeepResearch,
    AgentResearchSummary,
    SemanticKnowledgeGraph,
    LocalModelRouting
}

public sealed record NexusFabricStatus(
    bool IsAvailable,
    string Product,
    string Version,
    IReadOnlyList<NexusFabricFeature> Features,
    string Message);

public sealed record NexusFabricRequest(
    string Operation,
    string PayloadJson,
    string CorrelationId)
{
    public static NexusFabricRequest Create<T>(string operation, T payload) =>
        new(operation, JsonSerializer.Serialize(payload), Guid.NewGuid().ToString("N"));
}

public sealed record NexusFabricResponse(
    bool Success,
    string PayloadJson,
    string? Error,
    string CorrelationId)
{
    public T? ReadPayload<T>() => Success
        ? JsonSerializer.Deserialize<T>(PayloadJson)
        : default;
}

/// <summary>
/// Единственная типизированная точка связи оболочки с открытой реализацией Fabric.
/// Реализация не должна получать cookies, токены, пароли или значения полей форм.
/// </summary>
public interface INexusIntelligenceFabric : IDisposable
{
    void Initialize(INexusFabricHost host);

    NexusFabricStatus GetStatus();

    Task<NexusFabricResponse> ExecuteAsync(
        NexusFabricRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Узкий локальный мост. Fabric может вызвать уже упакованные модели, но не
/// получает WebView2, сеть, файловый профиль, cookies, формы и учётные данные.
/// </summary>
public interface INexusFabricHost
{
    Task<string> AskLocalTextModelAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<float>> EmbedLocallyAsync(
        string text,
        CancellationToken cancellationToken = default);
}

public sealed record NexusResearchDocument(
    string Id,
    string Title,
    string Url,
    string Text,
    int SourceRank);

public sealed record NexusDeepAnalysisRequest(
    string Question,
    NexusResearchDocument Document);

public sealed record NexusDeepResearchRequest(
    string Query,
    IReadOnlyList<NexusResearchDocument> Documents,
    int MaximumSources);

public sealed record NexusAgentSummaryRequest(
    string Query,
    IReadOnlyList<NexusResearchDocument> Documents,
    IReadOnlyList<string> AgentNotes);

public sealed record NexusResearchFinding(
    string Claim,
    IReadOnlyList<string> SourceIds,
    string Confidence);

public sealed record NexusAgentSummary(
    string Summary,
    IReadOnlyList<NexusResearchFinding> Findings,
    IReadOnlyList<string> Conflicts,
    IReadOnlyList<string> MissingInformation,
    string Recommendation);

public static class NexusFabricOperations
{
    public const string SmartOmniboxAnswer = "omnibox.answer.v1";
    public const string DeepPageAnswer = "page.answer.v1";
    public const string DeepPageAnalysis = "page.deep-analysis.v1";
    public const string InformationValueReport = "page.value-report.v1";
    public const string DeepResearch = "research.deep-search.v1";
    public const string AgentResearchSummary = "research.summary.v1";
    public const string KnowledgeGraphLink = "knowledge.link.v1";
}
