using System.Security.Cryptography;
using System.Text;

namespace Nexus.Guardian;

/// <summary>
/// Serializes interactive Guardian launches per installation directory. Portable
/// copies in different folders remain independent, while repeated clicks on the
/// same copy cannot start browsers against one shared data profile.
/// </summary>
internal sealed class GuardianSingleInstance : IDisposable
{
    private readonly Semaphore _semaphore;
    private bool _acquired;

    private GuardianSingleInstance(Semaphore semaphore)
    {
        _semaphore = semaphore;
        _acquired = true;
    }

    public static GuardianSingleInstance? TryAcquire(string applicationRoot,
        TimeSpan timeout = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationRoot);
        if (timeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));
        var semaphore = new Semaphore(1, 1, BuildName(applicationRoot));
        try
        {
            if (semaphore.WaitOne(timeout))
                return new GuardianSingleInstance(semaphore);
        }
        catch
        {
            semaphore.Dispose();
            throw;
        }

        semaphore.Dispose();
        return null;
    }

    internal static string BuildName(string applicationRoot)
    {
        var normalized = Path.GetFullPath(applicationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToUpperInvariant();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return @"Local\NexusMonach.Guardian." + Convert.ToHexString(hash);
    }

    public void Dispose()
    {
        if (_acquired)
        {
            _acquired = false;
            try { _semaphore.Release(); }
            catch (SemaphoreFullException) { }
        }
        _semaphore.Dispose();
    }
}
