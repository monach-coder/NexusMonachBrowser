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
    [InlineData("This is a complete sentence.", 1, true)]
    [InlineData("but warned that", 1, false)]
    [InlineData("but warned that inflation remains too high", 2, false)]
    [InlineData("but warned that inflation remains too high", 3, true)]
    [InlineData("one two three four five six seven eight nine", 1, false)]
    [InlineData("one two three four five six seven eight nine ten eleven twelve thirteen fourteen fifteen sixteen seventeen eighteen nineteen twenty", 1, true)]
    public void IncompleteTranscriptFragments_AreCombinedBeforeTranslation(
        string text, int fragments, bool expected)
    {
        Assert.Equal(expected,
            VideoSpeechTranslationContext.ShouldFlush(text, fragments));
    }

    [Fact]
    public void TranscriptFragments_KeepNaturalWordBoundary()
    {
        Assert.Equal("but warned that inflation remains high",
            VideoSpeechTranslationContext.JoinFragments(
                "but warned that", "inflation remains high"));
    }

    [Fact]
    public void BalancedContextWindow_KeepsEightRecentPhrasesForSeventyFiveSeconds()
    {
        var profile = VideoDubbingPolicy.ForMode(NexusMonach.Models.VideoTranslationMode.Balanced);
        var context = new VideoTranslationContextWindow(profile);
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 10; index++)
            context.Add(new VideoTranslationContextEntry(
                $"source {index}", $"перевод {index}", "en",
                now.AddSeconds(index - 10), now.AddSeconds(index - 9)));

        var bounded = context.Snapshot(now);
        Assert.Equal(8, bounded.Count);
        Assert.Equal("source 2", bounded[0].Transcript);
        Assert.Empty(context.Snapshot(now.AddSeconds(profile.ContextSeconds + 1)));
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

    [Theory]
    [InlineData("This is the most durable iPhone ever", "iPhone")]
    [InlineData("Ceramic Shield protects the front", "front")]
    [InlineData("It is more scratch resistant", "resistant")]
    public void RecentPhraseGuard_BlocksShortOverlapTails(string completePhrase, string overlapTail)
    {
        var guard = new RecentVideoPhraseGuard();
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.IsNovel(completePhrase, now));
        Assert.False(guard.IsNovel(overlapTail, now.AddSeconds(1)));
    }

    [Theory]
    [InlineData("The camera is ready.", "Камера готова.", true)]
    [InlineData("The camera is ready.", "Камера готова........В В В В В В В В В В", false)]
    [InlineData("For vivid images and sharp details.",
        "Для ярких изображений и деталей............................................................", false)]
    [InlineData("Use the applications you already know.",
        "Используйте приложения, которые вы уже знаете.", true)]
    public void VideoTranslationQuality_RejectsRunawayModelOutput(
        string source, string translated, bool expected)
    {
        Assert.Equal(expected,
            LocalIntelligenceService.ValidateVideoTranslation(source, translated).Length > 0);
    }

    [Fact]
    public void VideoLanguageTracker_LocksEnglishAcrossNoisyShortWindows()
    {
        var tracker = new VideoSourceLanguageTracker();

        Assert.Equal("en", tracker.Observe("We had to start again.", "english"));
        Assert.Equal("en", tracker.Observe("The camera is ready.", "english"));
        Assert.Equal("en", tracker.Observe("走 So, it wakes fast.", "chinese"));
        Assert.Equal("en", tracker.Observe("So das mag Bookmail.", "german"));
    }

    [Fact]
    public void VideoLanguageTracker_DoesNotForceFirstGermanPhraseToEnglish()
    {
        var tracker = new VideoSourceLanguageTracker();

        Assert.Equal("de", tracker.Observe("Guten Tag und willkommen.", "german"));
    }

    [Fact]
    public void VideoTranslation_SplitsMultipleSentencesWithoutLosingTheirOrder()
    {
        var units = LocalIntelligenceService.SplitVideoTranslationUnits(
            "Payment is made on site. Somebody will wait for you there. Really?");

        Assert.Equal(3, units.Count);
        Assert.Equal("Payment is made on site.", units[0]);
        Assert.Equal("Somebody will wait for you there.", units[1]);
        Assert.Equal("Really?", units[2]);
    }

}
