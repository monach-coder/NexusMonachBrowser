using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class NeuralVoiceLifecycleTests
{
    [Fact]
    public void StopIsSafeAndIdempotentWithoutInstalledVoicePack()
    {
        NeuralVoiceService.Stop();
        NeuralVoiceService.Stop();
    }
}
