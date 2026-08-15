using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class PageNarrationPolicyTests
{
    [Fact]
    public void MainArticleNarration_IsOrderedAndBounded()
    {
        var chunks = PageNarrationPolicy.CreateSpeechChunks(
        [
            "Первый абзац содержит основную мысль статьи.",
            "Второй абзац продолжает объяснение без элементов меню.",
            new string('я', 310)
        ]);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.InRange(
            chunk.Length, 1, PageNarrationPolicy.MaximumSpeechCharacters));
        Assert.StartsWith("Первый абзац", chunks[0], StringComparison.Ordinal);
    }

    [Fact]
    public void OperationTimeout_GrowsForLongArticlesButRemainsBounded()
    {
        var shortPage = PageNarrationPolicy.SelectOperationTimeout(100, 2);
        var longPage = PageNarrationPolicy.SelectOperationTimeout(9_000, 48);

        Assert.Equal(PageNarrationPolicy.MinimumOperationTimeout, shortPage);
        Assert.True(longPage > shortPage);
        Assert.True(longPage <= PageNarrationPolicy.MaximumOperationTimeout);
    }
}
