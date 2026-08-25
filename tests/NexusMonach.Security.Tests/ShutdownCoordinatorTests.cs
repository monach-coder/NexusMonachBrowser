using System.Diagnostics;
using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class ShutdownCoordinatorTests
{
    [Fact]
    public void CompletedStep_ReturnsTrue()
    {
        var invoked = false;

        var result = ShutdownCoordinator.RunStep("test-fast", () => invoked = true,
            TimeSpan.FromSeconds(1));

        Assert.True(result);
        Assert.True(invoked);
    }

    [Fact]
    public void StuckStep_ReturnsWithinItsBudget()
    {
        using var release = new ManualResetEventSlim(false);
        var timer = Stopwatch.StartNew();
        try
        {
            var result = ShutdownCoordinator.RunStep("test-timeout", release.Wait,
                TimeSpan.FromMilliseconds(80));

            Assert.False(result);
            Assert.InRange(timer.ElapsedMilliseconds, 50, 750);
        }
        finally { release.Set(); }
    }
}
