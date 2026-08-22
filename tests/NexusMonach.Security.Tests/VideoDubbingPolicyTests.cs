using NexusMonach.Services;
using NexusMonach.Models;
using NAudio.Wave;

namespace NexusMonach.Security.Tests;

public sealed class VideoDubbingPolicyTests
{
    [Fact]
    public void PrimaryDubbingPath_IsVoiceOnlyBoundedAndDoesNotPauseVideo()
    {
        Assert.False(VideoDubbingPolicy.UsesDomSubtitles);
        Assert.False(VideoDubbingPolicy.ShouldPausePlayback(directMediaCaptureAvailable: true));
        Assert.InRange(VideoDubbingPolicy.SegmentMilliseconds, 3_000, 3_500);
        Assert.InRange(VideoDubbingPolicy.SegmentOverlapMilliseconds, 700, 900);
        Assert.InRange(VideoDubbingPolicy.MaxBufferedSegments, 5, 8);
        Assert.InRange(VideoDubbingPolicy.MaxSegmentAgeMilliseconds, 10_000, 15_000);
        Assert.InRange(VideoDubbingPolicy.OriginalVolume, 0.08, 0.16);
        Assert.InRange(VideoDubbingPolicy.DirectSilenceProbeLimit, 2, 4);
        Assert.InRange(VideoDubbingPolicy.FirstLoopbackSegmentTimeoutMilliseconds, 8_000, 15_000);
    }

    [Fact]
    public void EndpointLoopbackFallback_NeverPausesVideo()
    {
        Assert.False(VideoDubbingPolicy.ShouldPausePlayback(directMediaCaptureAvailable: false));
    }

    [Fact]
    public void BufferingPauseBudgets_AreComfortableForViewer()
    {
        // Первая пауза — «подождите минутку»: не дольше полутора минут.
        Assert.InRange(VideoDubbingPolicy.InitialBufferWallBudgetSeconds, 45, 90);
        // Догрузка должна быть короче первой паузы, но успевать дать задел.
        Assert.InRange(VideoDubbingPolicy.CatchUpWallBudgetSeconds, 20,
            VideoDubbingPolicy.InitialBufferWallBudgetSeconds);
        Assert.InRange(VideoDubbingPolicy.InitialLookaheadSeconds, 90, 300);
        Assert.InRange(VideoDubbingPolicy.CatchUpLookaheadSeconds, 45,
            VideoDubbingPolicy.InitialLookaheadSeconds);
        // Выше ×8 захват звука деградирует — потолок обязан быть ограничен.
        Assert.InRange(VideoDubbingPolicy.MaximumAnalysisRate, 2, 8);
    }

    [Fact]
    public void VideoTranslationModes_HaveBoundedContextAndPreparedAudioReserve()
    {
        var fast = VideoDubbingPolicy.ForMode(VideoTranslationMode.Fast);
        var balanced = VideoDubbingPolicy.ForMode(VideoTranslationMode.Balanced);
        var quality = VideoDubbingPolicy.ForMode(VideoTranslationMode.Quality);

        Assert.Equal(VideoTranslationMode.Balanced, new BrowserSettings().VideoTranslationMode);
        Assert.InRange(fast.ContextSeconds, 60, 90);
        Assert.InRange(balanced.ContextSeconds, 60, 90);
        Assert.InRange(quality.ContextSeconds, 60, 90);
        Assert.InRange(fast.ContextPhrases, 6, 12);
        Assert.InRange(balanced.ContextPhrases, 6, 12);
        Assert.InRange(quality.ContextPhrases, 6, 12);
        Assert.True(fast.StartupPreparedSeconds < balanced.StartupPreparedSeconds);
        Assert.True(balanced.StartupPreparedSeconds < quality.StartupPreparedSeconds);
        Assert.True(fast.SegmentMilliseconds < balanced.SegmentMilliseconds);
        Assert.True(balanced.SegmentMilliseconds < quality.SegmentMilliseconds);
        Assert.Equal(2, fast.MaximumPendingParts);
        Assert.Equal(12, fast.MaximumPendingWords);
        Assert.Equal(3, balanced.MaximumPendingParts);
        Assert.Equal(12, balanced.MaximumPendingWords);
        Assert.All(new[] { fast, balanced, quality }, profile =>
            Assert.Equal(1, profile.StartupPreparedPhrases));
    }

    [Theory]
    [InlineData("The camera is ready.", 1, true)]
    [InlineData("camera we have ever made.", 1, false)]
    [InlineData("camera we have ever made. for everyone.", 2, true)]
    [InlineData("iPhone is ready.", 1, true)]
    public void LowercaseOverlapTails_WaitForOneFollowingWindow(
        string text, int parts, bool expected)
    {
        var fast = VideoDubbingPolicy.ForMode(VideoTranslationMode.Fast);

        Assert.Equal(expected,
            VideoDubbingPolicy.ShouldFinalizeUtterance(text, parts, fast));
    }

    [Fact]
    public void BalancedMode_AdaptsToShortClipsAndFeatureLengthVideo()
    {
        Assert.Equal(VideoTranslationMode.Fast,
            VideoDubbingPolicy.SelectEffectiveMode(VideoTranslationMode.Balanced, 19));
        Assert.Equal(VideoTranslationMode.Balanced,
            VideoDubbingPolicy.SelectEffectiveMode(VideoTranslationMode.Balanced, 10 * 60));
        Assert.Equal(VideoTranslationMode.Quality,
            VideoDubbingPolicy.SelectEffectiveMode(VideoTranslationMode.Balanced, 2 * 60 * 60));
        Assert.Equal(VideoTranslationMode.Fast,
            VideoDubbingPolicy.SelectEffectiveMode(VideoTranslationMode.Fast, 2 * 60 * 60));
        Assert.Equal(VideoTranslationMode.Balanced,
            VideoDubbingPolicy.SelectEffectiveMode(VideoTranslationMode.Balanced, null));
    }

    [Theory]
    [InlineData(VideoTranslationMode.Fast)]
    [InlineData(VideoTranslationMode.Balanced)]
    [InlineData(VideoTranslationMode.Quality)]
    public void LiveTtsTextAndAudio_AreBounded(VideoTranslationMode mode)
    {
        var profile = VideoDubbingPolicy.ForMode(mode);
        var text = VideoDubbingPolicy.PrepareTtsText(new string('я', 360), profile);

        Assert.InRange(text.Length, 1, profile.MaximumTtsCharacters + 1);
        Assert.True(VideoDubbingPolicy.IsPreparedAudioAcceptable(
            TimeSpan.FromSeconds(profile.MaximumPreparedAudioSeconds), profile));
        Assert.False(VideoDubbingPolicy.IsPreparedAudioAcceptable(
            TimeSpan.FromSeconds(profile.MaximumPreparedAudioSeconds + 0.01), profile));
    }

    [Fact]
    public void LongTranslations_AreSplitIntoChunksWithoutLosingContent()
    {
        var profile = VideoDubbingPolicy.ForMode(VideoTranslationMode.Balanced);
        var sentence = "Это достаточно длинное русское предложение переведённого текста.";
        // Пять предложений: больше шести сотен символов, но меньше жёсткого
        // лимита санитайзера объявлений в 360 символов.
        var text = string.Join(" ", Enumerable.Repeat(sentence, 5));

        var chunks = VideoDubbingPolicy.SplitTtsText(text, profile);

        Assert.True(chunks.Count >= 2);
        Assert.All(chunks, chunk => Assert.InRange(chunk.Length, 10, profile.MaximumTtsCharacters));
        var originalWords = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var spokenWords = string.Join(' ', chunks)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        // Ни одно слово перевода не теряется: обрезка с «…» заменена делением.
        Assert.Equal(originalWords.Length, spokenWords.Length);
        Assert.Equal(originalWords, spokenWords);
    }

    [Fact]
    public void ShortTranslation_IsReturnedAsSingleChunk()
    {
        var profile = VideoDubbingPolicy.ForMode(VideoTranslationMode.Balanced);

        var chunks = VideoDubbingPolicy.SplitTtsText("Привет и добро пожаловать.", profile);

        var chunk = Assert.Single(chunks);
        Assert.Equal("Привет и добро пожаловать.", chunk);
    }

    [Fact]
    public void PlaybackFailureTolerance_SkipsIsolatedFailuresInsteadOfKillingSession()
    {
        Assert.InRange(VideoDubbingPolicy.MaxConsecutivePlaybackFailures, 2, 8);
    }

    [Fact]
    public void RepeatedTranslations_AreSuppressedOnlyInsideShortWindow()
    {
        var guard = new RecentVideoPhraseGuard(capacity: 8, retentionSeconds: 15);
        var now = DateTimeOffset.UtcNow;

        Assert.True(guard.IsNovel("посмотрите внимательно на этот график", now));
        // Повторное распознавание того же звука приходит секундами позже.
        Assert.False(guard.IsNovel("посмотрите внимательно на этот график", now.AddSeconds(3)));
        // Честный повтор говорящего позже окна должен прозвучать целиком.
        Assert.True(guard.IsNovel("посмотрите внимательно на этот график", now.AddSeconds(40)));
    }

    [Fact]
    public void PrecomputeProfile_WaitsForCompleteSentencesAndBoundedAnalysisRate()
    {
        var profile = VideoDubbingPolicy.ForPrecompute();

        // Анализ идёт заранее: реплика ждёт конца предложения, а не лимита
        // на бегу — переводчик получает целые фразы, а не обрывки.
        Assert.True(profile.MaximumPendingWords >= 40);
        Assert.True(profile.MaximumPendingCharacters >= 280);
        Assert.True(profile.MaximumTtsCharacters > 140);
        Assert.InRange(VideoDubbingPolicy.MaximumAnalysisRate, 8, 16);
        var longUnfinished = string.Join(' ', Enumerable.Repeat("слово", 30));
        Assert.False(VideoDubbingPolicy.ShouldFinalizeUtterance(longUnfinished, 2, profile));
        Assert.True(VideoDubbingPolicy.ShouldFinalizeUtterance(
            "Полное развёрнутое предложение переведено целиком.", 3, profile));
    }

    [Fact]
    public void BacklogShedding_BoundsAccumulatedDubbingLagPerMode()
    {
        var fast = VideoDubbingPolicy.ForMode(VideoTranslationMode.Fast);
        var balanced = VideoDubbingPolicy.ForMode(VideoTranslationMode.Balanced);
        var quality = VideoDubbingPolicy.ForMode(VideoTranslationMode.Quality);

        // Русская озвучка длиннее исходной речи: без выталкивания старых реплик
        // очередь дорастает до минут и продолжает говорить после остановки.
        // Лимит запаса равен отставанию в стационарном режиме, поэтому он
        // держится минимально комфортным, а не «сколько влезет».
        Assert.InRange(fast.MaximumBufferedAudioSeconds, 5, 7);
        Assert.InRange(balanced.MaximumBufferedAudioSeconds, 7, 9);
        Assert.InRange(quality.MaximumBufferedAudioSeconds, 10, 14);
        Assert.True(VideoDubbingPolicy.ShouldShedTranslation(
            balanced.MaximumBufferedAudioSeconds, balanced));
        Assert.False(VideoDubbingPolicy.ShouldShedTranslation(
            balanced.MaximumBufferedAudioSeconds - 0.01, balanced));
        Assert.False(VideoDubbingPolicy.ShouldShedTranslation(0, balanced));
    }

    [Fact]
    public void ReadyAudioReserve_TracksPreparedWavDuration()
    {
        var reserve = new ReadyAudioReserve();
        reserve.Add(TimeSpan.FromSeconds(2.5));
        reserve.Add(TimeSpan.FromSeconds(3.25));

        Assert.Equal(2, reserve.Snapshot().Phrases);
        Assert.Equal(5.75, reserve.Snapshot().Seconds, precision: 3);

        reserve.Remove(TimeSpan.FromSeconds(2.5));
        Assert.Equal(1, reserve.Snapshot().Phrases);
        Assert.Equal(3.25, reserve.Snapshot().Seconds, precision: 3);
    }

    [Fact]
    public void ProcessLoopbackActivation_UsesDedicatedMtaThread()
    {
        Assert.True(ProcessAudioCaptureService.UsesDedicatedMtaActivation);
    }

    [Fact]
    [Trait("Category", "WindowsAudioIntegration")]
    public async Task ProcessLoopbackActivation_CanOpenAnIsolatedSession()
    {
        Assert.True(OperatingSystem.IsWindows());
        Assert.True(VideoDubbingPolicy.SupportsProcessLoopback(
            Environment.OSVersion.Version, Environment.ProcessId));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        await using var session = await ProcessAudioCaptureService.StartAsync(
            Environment.ProcessId, VideoDubbingPolicy.SegmentMilliseconds,
            VideoDubbingPolicy.SegmentOverlapMilliseconds, budget.Token);

        Assert.True(session.IsProcessIsolated);
    }

    [Fact]
    public void SilentDirectCaptureIsNotAcceptedAsUsableAudio()
    {
        Assert.True(VideoDubbingPolicy.IsSilentDirectCapture(success: true, wavBase64: string.Empty));
        Assert.False(VideoDubbingPolicy.HasUsableDirectAudio(success: true, wavBase64: string.Empty));
        Assert.True(VideoDubbingPolicy.HasUsableDirectAudio(success: true, wavBase64: "UklGRg=="));
        Assert.False(VideoDubbingPolicy.IsSilentDirectCapture(success: false, wavBase64: string.Empty));
    }

    [Fact]
    public void ProcessLoopback_RequiresSupportedWindowsAndTargetProcess()
    {
        Assert.True(VideoDubbingPolicy.SupportsProcessLoopback(new Version(10, 0, 20348), 42));
        Assert.True(VideoDubbingPolicy.SupportsProcessLoopback(new Version(10, 0, 26200), 42));
        Assert.False(VideoDubbingPolicy.SupportsProcessLoopback(new Version(10, 0, 20347), 42));
        Assert.False(VideoDubbingPolicy.SupportsProcessLoopback(new Version(10, 0, 26200), 0));
    }

    [Fact]
    public void StaleSegments_AreDroppedInsteadOfSpokenOutOfSync()
    {
        var now = DateTimeOffset.UtcNow;
        Assert.True(VideoDubbingPolicy.IsFresh(now.AddMilliseconds(-2_000), now));
        Assert.False(VideoDubbingPolicy.IsFresh(
            now.AddMilliseconds(-VideoDubbingPolicy.MaxSegmentAgeMilliseconds - 1), now));
        Assert.Equal(0, VideoDubbingPolicy.SelectSpeechRate(30));
        Assert.Equal(0, VideoDubbingPolicy.SelectSpeechRate(120));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0.00005, 0.0003, false)]
    [InlineData(0.0002, 0.0008, true)]
    [InlineData(0.00005, 0.002, true)]
    public void QuietTrackGate_AcceptsLowVolumeSpeechWithoutAcceptingDigitalSilence(
        double rms, double peak, bool expected)
    {
        Assert.Equal(expected, VideoDubbingPolicy.IsAudible(rms, peak));
    }

    [Fact]
    public void QuietTrackNormalization_IsBoundedAndAvoidsClipping()
    {
        var gain = VideoDubbingPolicy.SelectRecognitionGain(0.001, 0.02);

        Assert.InRange(gain, 1, VideoDubbingPolicy.MaximumRecognitionGain);
        Assert.True(0.02 * gain <= 0.94 + 0.000001);
        Assert.Equal(1, VideoDubbingPolicy.SelectRecognitionGain(0, 0));
    }

    [Fact]
    public void QuietLoopbackPcm_IsAmplifiedInsideWhisperWav()
    {
        var format = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        var raw = new byte[48_000 * format.BlockAlign];
        for (var frame = 0; frame < 48_000; frame++)
        {
            var sample = (float)(Math.Sin(frame * Math.PI * 2 * 440 / 48_000) * 0.002);
            BitConverter.GetBytes(sample).CopyTo(raw, frame * format.BlockAlign);
            BitConverter.GetBytes(sample).CopyTo(raw, frame * format.BlockAlign + sizeof(float));
        }

        var converted = SystemAudioCaptureService.ConvertRawToWav(raw, format);
        using var reader = new WaveFileReader(new MemoryStream(converted.Wav));
        var samples = reader.ToSampleProvider();
        var buffer = new float[16_000];
        var read = samples.Read(buffer, 0, buffer.Length);

        Assert.True(VideoDubbingPolicy.IsAudible(converted.Rms, converted.Peak));
        Assert.True(read > 10_000);
        Assert.True(buffer.Take(read).Max(sample => Math.Abs(sample)) > 0.015);
    }
}
