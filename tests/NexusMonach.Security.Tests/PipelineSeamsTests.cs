using NexusMonach.Services;
using Xunit;

namespace NexusMonach.Security.Tests;

public sealed class PipelineSeamsTests
{
    [Fact]
    public void Ui_WithoutApplication_RunsInline()
    {
        // Юнит-тесты идут без WPF-приложения: шлюз обязан исполнять действие синхронно.
        var ran = false;
        Ui.Invoke(() => ran = true);
        Assert.True(ran);
    }

    [Fact]
    public void Ui_PostWithoutApplication_RunsInline()
    {
        var ran = false;
        Ui.Post(() => ran = true);
        Assert.True(ran);
    }

    [Fact]
    public async Task Recognizer_SeamIsReplaceable()
    {
        var calls = new List<byte[]>();
        AiPipeline.ISpeechRecognizer original = AiPipeline.Recognizer;
        AiPipeline.Recognizer = new FakeRecognizer(calls);
        try
        {
            var result = await AiPipeline.Recognizer.TranscribeAsync([1, 2, 3]);
            Assert.Equal("распознано", result);
            Assert.Single(calls);
        }
        finally
        {
            AiPipeline.Recognizer = original;
        }
    }

    [Fact]
    public async Task Translator_SeamIsReplaceable()
    {
        AiPipeline.ITextTranslator original = AiPipeline.Translator;
        AiPipeline.Translator = new FakeTranslator();
        try
        {
            var result = await AiPipeline.Translator.TranslateToRussianAsync("hello", sourceIsEnglish: true);
            Assert.Equal("переведено: hello", result);
        }
        finally
        {
            AiPipeline.Translator = original;
        }
    }

    [Fact]
    public void Voice_SeamIsReplaceable()
    {
        var announced = new List<string>();
        AiPipeline.IVoiceAnnouncer original = AiPipeline.Voice;
        AiPipeline.Voice = new FakeVoice(announced);
        try
        {
            var spoken = AiPipeline.Voice.Announce("тест", VoiceAnnouncementPriority.Critical);
            Assert.True(spoken);
            Assert.Equal(["тест"], announced);
        }
        finally
        {
            AiPipeline.Voice = original;
        }
    }

    private sealed class FakeRecognizer(List<byte[]> calls) : AiPipeline.ISpeechRecognizer
    {
        public Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken = default)
        {
            calls.Add(wav);
            return Task.FromResult("распознано");
        }
    }

    private sealed class FakeTranslator : AiPipeline.ITextTranslator
    {
        public Task<string> TranslateToRussianAsync(string text, bool sourceIsEnglish = false,
            CancellationToken cancellationToken = default, string? sourceLanguage = null) =>
            Task.FromResult("переведено: " + text);
    }

    private sealed class FakeVoice(List<string> sink) : AiPipeline.IVoiceAnnouncer
    {
        public bool Announce(string text,
            VoiceAnnouncementPriority priority = VoiceAnnouncementPriority.Important,
            bool isPrivateWindow = false)
        {
            sink.Add(text);
            return true;
        }
    }
}
