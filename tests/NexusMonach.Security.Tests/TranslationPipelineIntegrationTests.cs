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
    public async Task PageSegments_PreserveNodeIdsAndTranslateEveryFragment()
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
    }

    [Fact]
    [Trait("Category", "FullOfflineTranslation")]
    public async Task VideoAudio_IsRecognizedThenTranslatedToRussianSubtitle()
    {
        if (!RequireTranslationPayload() || !RequireSpeechPayload()) return;

        var wav = SynthesizeEnglishWav(
            "This browser protects your privacy and translates video locally.");
        using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(5));

        var transcript = await WhisperService.TranscribeDetailedAsync(wav, budget.Token);
        Assert.False(string.IsNullOrWhiteSpace(transcript.Text));

        var subtitle = await LocalIntelligenceService.TranslateToRussianAsync(
            transcript.Text, budget.Token,
            string.IsNullOrWhiteSpace(transcript.Language) ? "en" : transcript.Language);

        AssertValidRussian(subtitle);
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
