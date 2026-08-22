using Nexus.Guardian;

namespace NexusMonach.Security.Tests;

public sealed class GuardianIntegrityTests
{
    [Fact]
    public void EcdsaSignedManifest_VerifiesAndRejectsTampering()
    {
        using var fixture = new IntegrityFixture();
        fixture.CreateSignedManifest();

        var verified = IntegrityVerifier.Verify(fixture.PayloadDirectory, full: true);
        Assert.Equal(IntegrityState.Verified, verified.State);

        File.AppendAllText(Path.Combine(fixture.PayloadDirectory, IntegrityVerifier.ManifestName), " ");
        var tampered = IntegrityVerifier.Verify(fixture.PayloadDirectory, full: true);

        Assert.Equal(IntegrityState.InvalidSignature, tampered.State);
    }

    [Fact]
    public void SignedManifest_DetectsCriticalFileHashMismatch()
    {
        using var fixture = new IntegrityFixture();
        fixture.CreateSignedManifest();
        File.WriteAllText(Path.Combine(fixture.PayloadDirectory, "NexusMonach.Browser.exe"), "modified payload");

        var result = IntegrityVerifier.Verify(fixture.PayloadDirectory, full: true);

        Assert.Equal(IntegrityState.CriticalMismatch, result.State);
    }

    [Fact]
    public void NestedMimosaToolState_DoesNotBlockVerification()
    {
        using var fixture = new IntegrityFixture();
        fixture.CreateSignedManifest();
        // Внешние инструменты разработки могут оставить служебный каталог
        // .mimosa в любой подпапке (например, AI/.mimosa/hook-status) уже
        // после подписи — это не часть поставки и не повод для блокировки.
        Directory.CreateDirectory(Path.Combine(fixture.PayloadDirectory, "AI", ".mimosa", "hook-status"));
        File.WriteAllText(
            Path.Combine(fixture.PayloadDirectory, "AI", ".mimosa", "hook-status", "session.json"),
            "{\"status\":\"ok\"}");

        var result = IntegrityVerifier.Verify(fixture.PayloadDirectory, full: true);

        Assert.Equal(IntegrityState.Verified, result.State);
    }

    private sealed class IntegrityFixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "NexusGuardianTests", Guid.NewGuid().ToString("N"));
        public string PayloadDirectory => Path.Combine(_root, "payload");
        private string KeyDirectory => Path.Combine(_root, "keys");

        public IntegrityFixture()
        {
            Directory.CreateDirectory(PayloadDirectory);
            Directory.CreateDirectory(KeyDirectory);
            var executable = Environment.ProcessPath
                             ?? throw new InvalidOperationException("Путь testhost.exe недоступен.");
            File.Copy(executable, Path.Combine(PayloadDirectory, "NexusMonach.Browser.exe"));
            IntegrityVerifier.GenerateKeyPair(KeyDirectory);
            File.Copy(
                Path.Combine(KeyDirectory, IntegrityVerifier.PublicKeyName),
                Path.Combine(PayloadDirectory, IntegrityVerifier.PublicKeyName));
        }

        public void CreateSignedManifest() => IntegrityVerifier.CreateManifest(
            PayloadDirectory,
            Path.Combine(KeyDirectory, "integrity-private-key.pem"));

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); }
            catch { }
        }
    }
}
