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
        Assert.InRange(VideoDubbingPolicy.MaxBufferedSegments, 2, 4);
        Assert.InRange(VideoDubbingPolicy.MaxSegmentAgeMilliseconds, 7_000, 10_000);
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
