using NexusMonach.Intelligence;

namespace NexusMonach.Services;

internal sealed class NexusFabricHostAdapter : INexusFabricHost
{
    public async Task<string> AskLocalTextModelAsync(
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken = default)
    {
        if (systemPrompt.Length > 12_000 || userPrompt.Length > 80_000)
            throw new InvalidOperationException("Fabric превысил локальный лимит контекста.");
        var model = await LocalAiService.GetPreferredModelAsync(cancellationToken)
                    ?? throw new InvalidOperationException(AiModelCatalog.MissingTextRuntimeMessage);
        return await LocalAiService.AskAsync(model, systemPrompt, userPrompt, cancellationToken);
    }

    public async Task<IReadOnlyList<float>> EmbedLocallyAsync(
        string text,
        CancellationToken cancellationToken = default) =>
        await SemanticEmbeddingService.EmbedAsync(text, cancellationToken);
}
