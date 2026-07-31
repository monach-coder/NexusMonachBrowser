using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexus.Guardian;

internal static class CrashReportCrypto
{
    private static readonly byte[] AssociatedData = Encoding.UTF8.GetBytes("NexusGuardianCrashReport/v1");
    public static void GenerateKeyPair(string directory)
    {
        Directory.CreateDirectory(directory);
        using var rsa = RSA.Create(3072);
        File.WriteAllText(Path.Combine(directory, "crash-report-private-key.pem"),
            rsa.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "crash-report-public-key.pem"),
            rsa.ExportSubjectPublicKeyInfoPem(), new UTF8Encoding(false));
    }

    public static void Decrypt(string inputPath, string privateKeyPath, string outputPath)
    {
        var envelope = JsonSerializer.Deserialize<CrashReportEnvelope>(File.ReadAllBytes(inputPath))
            ?? throw new InvalidDataException("Пустой контейнер Nexus Guardian.");
        if (envelope.SchemaVersion != 1 ||
            !envelope.Algorithm.Equals("RSA-OAEP-SHA256+A256GCM", StringComparison.Ordinal))
            throw new InvalidDataException("Неподдерживаемый формат зашифрованного рапорта.");

        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        var key = rsa.Decrypt(Convert.FromBase64String(envelope.EncryptedKey), RSAEncryptionPadding.OaepSHA256);
        try
        {
            var nonce = Convert.FromBase64String(envelope.Nonce);
            var tag = Convert.FromBase64String(envelope.Tag);
            var cipherText = Convert.FromBase64String(envelope.CipherText);
            var plainText = new byte[cipherText.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(nonce, cipherText, tag, plainText, AssociatedData);

            var actualHash = Convert.ToHexString(SHA256.HashData(plainText)).ToLowerInvariant();
            if (!CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(actualHash), Encoding.ASCII.GetBytes(envelope.PlainTextSha256)))
                throw new CryptographicException("SHA-256 расшифрованного рапорта не совпадает.");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
            File.WriteAllBytes(outputPath, plainText);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    private sealed class CrashReportEnvelope
    {
        public int SchemaVersion { get; set; }
        public string Algorithm { get; set; } = string.Empty;
        public string EncryptedKey { get; set; } = string.Empty;
        public string Nonce { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string CipherText { get; set; } = string.Empty;
        public string PlainTextSha256 { get; set; } = string.Empty;
    }
}
