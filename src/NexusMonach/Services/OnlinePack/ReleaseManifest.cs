using System.Security.Cryptography;
using System.Text.Json;

namespace NexusMonach.Services.OnlinePack;

/// <summary>Один файл сетевой поставки: куда положить и каким хешем проверить.</summary>
public sealed class ReleaseFile
{
    public string RelativePath { get; set; } = string.Empty;
    public long Length { get; set; }
    public string Sha256 { get; set; } = string.Empty;
    /// <summary>"core" — ядро браузера; "ai" — модель, без которой браузер работает.</summary>
    public string Group { get; set; } = "ai";
    /// <summary>Человекочитаемое назначение — для прогресса и голоса.</summary>
    public string Purpose { get; set; } = string.Empty;
}

/// <summary>
/// Подписанный манифест сетевой поставки. Тот же механизм доверия, что у
/// локальной целостности Guardian: ECDSA P-256 + SHA-256; компрометация
/// зеркала не даёт подсунуть вредоносный файл — подпись сломается.
/// </summary>
public sealed class ReleaseManifest
{
    public int SchemaVersion { get; set; } = 1;
    public string Product { get; set; } = "Nexus Monach";
    public string Version { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public List<ReleaseFile> Files { get; set; } = [];

    public static ReleaseManifest Parse(byte[] manifestBytes)
    {
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(manifestBytes);
        if (manifest is null || manifest.SchemaVersion != 1 || manifest.Files.Count == 0)
            throw new InvalidOperationException("Манифест поставки повреждён или неизвестной версии.");
        return manifest;
    }
}

public static class ReleaseManifestVerifier
{
    /// <summary>Проверяет подпись манифеста публичным ключом Guardian.</summary>
    public static bool Verify(byte[] manifestBytes, string signatureBase64, string publicKeyPem)
    {
        try
        {
            var signature = Convert.FromBase64String(signatureBase64);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(publicKeyPem);
            return ecdsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Хеш файла для сверки с манифестом. Бросает исключение при ошибке чтения.</summary>
    public static string ComputeSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
