using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class VideoSpeechTranslationBoundaryTests
{
    [Fact]
    public void TranslationProviderBoundary_CannotReturnRemoteAudio()
    {
        Assert.False(VideoSpeechTranslationService.AllowsRemoteSynthesizedAudio);
        Assert.True(VideoSpeechTranslationService.RequiresLocalVoiceOutput);
        Assert.DoesNotContain(typeof(VideoSpeechTranslationText).GetProperties(),
            property => property.PropertyType == typeof(byte[]));
    }

    [Theory]
    [InlineData("we need a better local voice", "local voice should remain private",
        "should remain private")]
    [InlineData("Hello, brave world!", "brave world! This is Nexus.", "This is Nexus.")]
    [InlineData("same phrase", "same phrase", "")]
    public void TranscriptWindows_RemoveRepeatedOverlap(
        string previous, string current, string expected)
    {
        Assert.Equal(expected,
            VideoSpeechTranslationService.RemoveTranscriptOverlap(previous, current));
    }

    [Theory]
    [InlineData("Это важная новость из Лондона", "Это важная новость из Лондона!", true)]
    [InlineData("Сегодня рынок заметно вырос", "рынок заметно вырос", true)]
    [InlineData("Президент выступил сегодня утром", "Сегодня утром выступил президент", true)]
    [InlineData("Первый сюжет закончился", "Начинается прогноз погоды", false)]
    public void NearDuplicatePhrases_AreRecognizedAcrossOverlappingWindows(
        string first, string second, bool expected)
    {
        Assert.Equal(expected, RecentVideoPhraseGuard.IsSamePhrase(first, second));
    }

    [Fact]
    public void RecentPhraseGuard_BlocksAbaLoopButExpiresOldSpeech()
    {
        var guard = new RecentVideoPhraseGuard(capacity: 4, retentionSeconds: 10);
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.IsNovel("Первая длинная переведённая фраза", now));
        Assert.True(guard.IsNovel("Совсем другая реплика диктора", now.AddSeconds(1)));
        Assert.False(guard.IsNovel("Первая длинная переведенная фраза!", now.AddSeconds(2)));
        Assert.True(guard.IsNovel("Первая длинная переведённая фраза", now.AddSeconds(11)));
    }
}
