using Nexus.Guardian;

namespace NexusMonach.Security.Tests;

public sealed class GuardianSingleInstanceTests
{
    [Fact]
    public void SameInstallationCannotBeAcquiredTwiceAndCanBeReopenedAfterDispose()
    {
        var root = Path.Combine(Path.GetTempPath(), "NexusGuardianInstanceTests", Guid.NewGuid().ToString("N"));
        var first = GuardianSingleInstance.TryAcquire(root);
        Assert.NotNull(first);

        try
        {
            using var duplicate = GuardianSingleInstance.TryAcquire(root + Path.DirectorySeparatorChar);
            Assert.Null(duplicate);
        }
        finally
        {
            first.Dispose();
        }

        using var reopened = GuardianSingleInstance.TryAcquire(root);
        Assert.NotNull(reopened);
    }

    [Fact]
    public void DifferentInstallationDirectoriesRemainIndependent()
    {
        var baseRoot = Path.Combine(Path.GetTempPath(), "NexusGuardianInstanceTests", Guid.NewGuid().ToString("N"));
        using var first = GuardianSingleInstance.TryAcquire(Path.Combine(baseRoot, "first"));
        using var second = GuardianSingleInstance.TryAcquire(Path.Combine(baseRoot, "second"));

        Assert.NotNull(first);
        Assert.NotNull(second);
    }

    [Fact]
    public async Task RestartHandoffCanWaitForPreviousInstanceToRelease()
    {
        var root = Path.Combine(Path.GetTempPath(), "NexusGuardianInstanceTests", Guid.NewGuid().ToString("N"));
        var first = GuardianSingleInstance.TryAcquire(root);
        Assert.NotNull(first);

        var waiting = Task.Run(() => GuardianSingleInstance.TryAcquire(root, TimeSpan.FromSeconds(2)));
        await Task.Delay(100);
        first.Dispose();

        using var handedOff = await waiting;
        Assert.NotNull(handedOff);
    }
}
