using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class VideoDubbingPolicyTests
{
    [Fact]
    public void PrimaryDubbingPath_IsVoiceOnlyBoundedAndDoesNotPauseVideo()
    {
        Assert.False(VideoDubbingPolicy.UsesDomSubtitles);
        Assert.False(VideoDubbingPolicy.ShouldPausePlayback(directMediaCaptureAvailable: true));
        Assert.InRange(VideoDubbingPolicy.DirectSegmentSeconds, 2, 4);
        Assert.InRange(VideoDubbingPolicy.MaxBufferedSegments, 1, 2);
        Assert.InRange(VideoDubbingPolicy.OriginalVolume, 0.1, 0.3);
    }

    [Fact]
    public void LoopbackFallback_MayPauseToPreventVoiceFeedback()
    {
        Assert.True(VideoDubbingPolicy.ShouldPausePlayback(directMediaCaptureAvailable: false));
    }
}
