using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NexusMonach.Services.OnlinePack;
using Xunit;

namespace NexusMonach.Security.Tests;

public sealed class OnlinePackTests
{
    private static (byte[] Manifest, string Signature, string PublicPem, string PrivatePem) SignedManifest()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest
        {
            Version = "2.9.0.0",
            CreatedUtc = DateTimeOffset.UtcNow,
            Files =
            [
                new ReleaseFile
                {
                    RelativePath = "AI/models/whisper/model.bin",
                    Length = 1234,
                    Sha256 = "ab12",
                    Group = "ai",
                    Purpose = "распознавание речи whisper"
                }
            ]
        });
        var signature = Convert.ToBase64String(ecdsa.SignData(manifest, HashAlgorithmName.SHA256));
        return (manifest, signature,
            ecdsa.ExportSubjectPublicKeyInfoPem(), ecdsa.ExportPkcs8PrivateKeyPem());
    }

    [Fact]
    public void Signature_VerifiesForGenuineManifest()
    {
        var (manifest, signature, publicPem, _) = SignedManifest();
        Assert.True(ReleaseManifestVerifier.Verify(manifest, signature, publicPem));
    }

    [Fact]
    public void Signature_FailsForTamperedManifest()
    {
        var (manifest, signature, publicPem, _) = SignedManifest();
        var tampered = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest
        {
            Version = "99.0", CreatedUtc = DateTimeOffset.UtcNow,
            Files = [new ReleaseFile { RelativePath = "evil.exe", Length = 1, Sha256 = "00" }]
        });
        Assert.False(ReleaseManifestVerifier.Verify(tampered, signature, publicPem));
    }

    [Fact]
    public void Signature_FailsForGarbage()
    {
        var (_, _, publicPem, _) = SignedManifest();
        Assert.False(ReleaseManifestVerifier.Verify("not-json"u8.ToArray(), "!!!not-base64!!!", publicPem));
    }

    [Fact]
    public void Manifest_ParsesAndRejectsWrongSchema()
    {
        var (manifest, _, _, _) = SignedManifest();
        var parsed = ReleaseManifest.Parse(manifest);
        Assert.Equal("2.9.0.0", parsed.Version);
        Assert.Single(parsed.Files);

        var bad = JsonSerializer.SerializeToUtf8Bytes(new ReleaseManifest { SchemaVersion = 9 });
        Assert.Throws<InvalidOperationException>(() => ReleaseManifest.Parse(bad));
    }

    [Fact]
    public void ComputeSha256_MatchesKnownVector()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexus-sha-test-" + Guid.NewGuid().ToString("N"));
        File.WriteAllBytes(path, "hello"u8.ToArray());
        try
        {
            using var sha = SHA256.Create();
            var expected = Convert.ToHexString(sha.ComputeHash("hello"u8.ToArray())).ToLowerInvariant();
            Assert.Equal(expected, ReleaseManifestVerifier.ComputeSha256(path));
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("распознавание речи whisper", "перевод видео")]
    [InlineData("голос silero", "голос помощника")]
    [InlineData("перевод страниц", "перевод страниц")]
    [InlineData("семантический поиск", "семантический поиск")]
    [InlineData("непонятно что", "компонент")]
    public void VoiceFriendlyName_MapsPurposes(string purpose, string expected)
    {
        Assert.Equal(expected, AiPackFetchService.VoiceFriendlyName(purpose));
    }
}
