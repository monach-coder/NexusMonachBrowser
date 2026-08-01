using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class TranslationRouteTests
{
    [Theory]
    [InlineData("This browser protects your privacy.", "", false, "en")]
    [InlineData("Neutral text", "en-US", false, "en")]
    [InlineData("이 브라우저는 로컬에서 번역합니다.", "", false, "ko")]
    [InlineData("Neutral text", "ko-KR", false, "ko")]
    [InlineData("Diese Seite enthält wichtige Informationen.", "de", false, "auto")]
    [InlineData("これは重要な情報です。", "ja", false, "auto")]
    [InlineData("Neutral text", "", true, "en")]
    public void SelectSourceRoute_ChoosesExpectedModelPath(
        string text, string language, bool sourceIsEnglish, string expected)
    {
        Assert.Equal(expected,
            TranslationService.SelectSourceRoute(text, language, sourceIsEnglish));
    }

    [Fact]
    public async Task EmptySelection_DoesNotStartModels()
    {
        Assert.Equal(string.Empty,
            await TranslationService.TranslateToRussianAsync("   "));
    }
}
