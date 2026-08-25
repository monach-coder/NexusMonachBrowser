using System.Speech.Synthesis;
using System.Text.RegularExpressions;
using NexusMonach.Models;
using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed partial class TranslationPipelineIntegrationTests : IDisposable
{
    private static bool FullOfflineTestsRequired =>
        Environment.GetEnvironmentVariable("NEXUS_REQUIRE_FULL_OFFLINE_AI_TESTS") == "1";

    [Fact]
    [Trait("Category", "FullOfflineTranslation")]
    public async Task SelectedFragment_IsTranslatedToValidRussianUtf8()
    {
        if (!RequireTranslationPayload()) return;

        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var translated = await LocalIntelligenceService.TranslateToRussianAsync(
            "The selected paragraph contains important security information.",
            budget.Token, "en");

        AssertValidRussian(translated);
    }

    [Fact]
    [Trait("Category", "FullOfflineTranslation")]
    public async Task PageArticleAndInteractiveSegments_PreserveIdsAndTranslateEveryFragment()
    {
        if (!RequireTranslationPayload()) return;

        var page = new[]
        {
            new TranslationSegment
            {
                Id = "heading-1", Language = "en",
                Text = "Privacy settings for this browser"
            },
            new TranslationSegment
            {
                Id = "paragraph-1", Language = "de",
                Text = "Diese Seite enthält wichtige Informationen über Sicherheit."
            },
            new TranslationSegment
            {
                Id = "button-1", Language = "ja",
                Text = "詳細情報を開く"
            }
        };
        var progressiveBatches = new List<IReadOnlyList<TranslationSegment>>();
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var translated = await LocalIntelligenceService.TranslateSegmentsAsync(
            page, budget.Token, batch =>
            {
                progressiveBatches.Add(batch.ToArray());
                return Task.CompletedTask;
            });

        Assert.Equal(page.Select(item => item.Id), translated.Select(item => item.Id));
        Assert.NotEmpty(progressiveBatches);
        Assert.Equal(page.Length, progressiveBatches.Sum(batch => batch.Count));
        Assert.All(translated, item => AssertValidRussian(item.Text));

        var articleIds = new[] { "heading-1", "paragraph-1" };
        var narration = PageNarrationPolicy.CreateSpeechChunks(
            translated.Where(item => articleIds.Contains(item.Id)).Select(item => item.Text));
        Assert.NotEmpty(narration);
        Assert.All(narration, chunk =>
        {
            AssertValidRussian(chunk);
            Assert.InRange(chunk.Length, 1, PageNarrationPolicy.MaximumSpeechCharacters);
        });

        // В интерактивный DOM возвращается только заранее отобранный элемент
        // интерфейса. Значения полей пользователя вообще не входят в пакет.
        var interactive = translated.Where(item => item.Id == "button-1").ToArray();
        Assert.Single(interactive);
        AssertValidRussian(interactive[0].Text);
        Assert.DoesNotContain(translated,
            item => item.Id.Contains("value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    [Trait("Category", "FullOfflineTranslation")]
    public async Task VideoAudio_IsRecognizedTranslatedAndPreparedForFemaleSpeech()
    {
        if (!RequireTranslationPayload() || !RequireSpeechPayload()) return;

        var wav = SynthesizeEnglishWav(
            "This browser protects your privacy and translates video locally.");
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var transcript = await WhisperService.TranscribeDetailedAsync(wav, budget.Token);
        Assert.False(string.IsNullOrWhiteSpace(transcript.Text));

        var translatedSpeech = await LocalIntelligenceService.TranslateToRussianAsync(
            transcript.Text, budget.Token,
            string.IsNullOrWhiteSpace(transcript.Language) ? "en" : transcript.Language);

        AssertValidRussian(translatedSpeech);
        var safeSpeech = VoiceAssistantService.SanitizeForSpeech(translatedSpeech);
        AssertValidRussian(safeSpeech);
        Assert.True(safeSpeech.Length <= 360);

        NexusVoiceCandidate[] installedVoices =
        [
            new("Russian male", "ru-RU", NexusVoiceGender.Male),
            new("English female", "en-US", NexusVoiceGender.Female),
            new("Russian female", "ru-RU", NexusVoiceGender.Female)
        ];
        var selectedVoice = VoiceProfileSelector.SelectPreferredIndex(installedVoices);
        Assert.Equal(NexusVoiceGender.Female, installedVoices[selectedVoice].Gender);
    }

    [Fact]
    [Trait("Category", "FullOfflineTranslation")]
    public async Task VideoPhrases_AreBoundedAndContainNoRunawayMarianTail()
    {
        if (!RequireTranslationPayload()) return;

        string[] phrases =
        [
            "For vivid images and sharp details.",
            "The multi-touch trackpad lets you click and scroll.",
            "And it is easy to add the apps you already use.",
            "iPhone 17 Pro lets you create setups that would be impossible."
        ];
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));

        foreach (var phrase in phrases)
        {
            var translated = await LocalIntelligenceService.TranslateVideoPhraseAsync(
                phrase, [], budget.Token, "en");

            AssertValidRussian(translated);
            Assert.InRange(translated.Length, 1, Math.Max(72, phrase.Length * 3 + 24));
            Assert.DoesNotMatch(@"\.{4,}", translated);
            Assert.DoesNotMatch(@"(?i)(?:\b[\p{L}]\b[\s,.;:!?]*){6,}", translated);
        }
    }

    private static bool RequireTranslationPayload()
    {
        if (AiModelCatalog.TranslationReady) return true;
        Assert.False(FullOfflineTestsRequired,
            "Full Offline translation payload is missing: " +
            AiModelCatalog.MissingTranslationRuntimeMessage);
        return false;
    }

    private static bool RequireSpeechPayload()
    {
        if (AiModelCatalog.SpeechReady) return true;
        Assert.False(FullOfflineTestsRequired,
            "Full Offline Whisper payload is missing: " +
            AiModelCatalog.MissingSpeechRuntimeMessage);
        return false;
    }

    private static byte[] SynthesizeEnglishWav(string text)
    {
        using var output = new MemoryStream();
        using var voice = new SpeechSynthesizer();
        voice.Rate = -1;
        voice.SetOutputToWaveStream(output);
        voice.Speak(text);
        return output.ToArray();
    }

    private static void AssertValidRussian(string value)
    {
        Assert.False(string.IsNullOrWhiteSpace(value));
        Assert.Matches(CyrillicRegex(), value);
        Assert.DoesNotContain('�', value);
        Assert.DoesNotContain("Рџ", value, StringComparison.Ordinal);
        Assert.DoesNotContain("РЎ", value, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading model", value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<|im_", value, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        TranslationService.Stop();
        WhisperService.Shutdown();
    }

    [GeneratedRegex("[\\u0400-\\u04FF]")]
    private static partial Regex CyrillicRegex();
}
