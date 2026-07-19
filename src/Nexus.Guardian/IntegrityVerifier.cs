using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Reflection;

namespace Nexus.Guardian;

internal static class IntegrityVerifier
{
#if GUARDIAN_OFFICIAL
    private const bool RequireSignature = true;
#else
    private const bool RequireSignature = false;
#endif
    internal const string ManifestName = "integrity-manifest.json";
    internal const string SignatureName = "integrity-manifest.sig";
    internal const string PublicKeyName = "integrity-public-key.pem";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static IntegrityResult Verify(string root, bool full)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var rootPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ||
                         normalizedRoot.EndsWith(Path.AltDirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;
        var manifestPath = Path.Combine(normalizedRoot, ManifestName);
        var signaturePath = Path.Combine(normalizedRoot, SignatureName);
        var publicKeyPath = Path.Combine(normalizedRoot, PublicKeyName);

        var embeddedKey = HasEmbeddedPublicKey();
        var hasManifest = File.Exists(manifestPath);
        var hasSignature = File.Exists(signaturePath);
        var hasPublicKey = embeddedKey || File.Exists(publicKeyPath);
        if ((!hasManifest || !hasSignature || !hasPublicKey) && !RequireSignature)
        {
            return new IntegrityResult
            {
                State = IntegrityState.DevelopmentBuild,
                Problems = ["Подписанный манифест отсутствует. Это допустимо только для локальной сборки разработчика."]
            };
        }
        if (!hasManifest || !hasSignature || !hasPublicKey)
        {
            return new IntegrityResult
            {
                State = IntegrityState.InvalidSignature,
                Problems = ["Подписанный манифест, подпись или доверенный открытый ключ отсутствуют."]
            };
        }

        byte[] manifestBytes;
        IntegrityManifest? manifest;
        try
        {
            manifestBytes = File.ReadAllBytes(manifestPath);
            manifest = JsonSerializer.Deserialize<IntegrityManifest>(manifestBytes);
            if (manifest is null || manifest.SchemaVersion != 1 || manifest.Files.Count == 0)
                throw new InvalidDataException("Некорректная структура манифеста.");
        }
        catch (Exception ex)
        {
            return new IntegrityResult { State = IntegrityState.InvalidSignature, Problems = ["Манифест не читается: " + ex.Message] };
        }

        try
        {
            var signatureText = File.ReadAllText(signaturePath).Trim();
            var signature = Convert.FromBase64String(signatureText);
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportFromPem(ReadTrustedPublicKey(publicKeyPath));
            if (!ecdsa.VerifyData(manifestBytes, signature, HashAlgorithmName.SHA256))
                return new IntegrityResult { State = IntegrityState.InvalidSignature, Problems = ["Подпись манифеста не совпадает."] };
        }
        catch (Exception ex)
        {
            return new IntegrityResult { State = IntegrityState.InvalidSignature, Problems = ["Подпись или открытый ключ повреждены: " + ex.Message] };
        }

        var criticalProblems = new List<string>();
        var otherProblems = new List<string>();
        var expectedPaths = manifest.Files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in manifest.Files)
        {
            var relative = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(normalizedRoot, relative));
            if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            {
                criticalProblems.Add("Недопустимый путь в манифесте: " + entry.Path);
                continue;
            }

            if (!File.Exists(fullPath))
            {
                (entry.Critical ? criticalProblems : otherProblems).Add("Отсутствует: " + entry.Path);
                continue;
            }

            var info = new FileInfo(fullPath);
            if (info.Length != entry.Length)
            {
                (entry.Critical ? criticalProblems : otherProblems).Add("Изменён размер: " + entry.Path);
                continue;
            }

            if (entry.Critical || full)
            {
                var actual = ComputeSha256(fullPath);
                if (!actual.Equals(entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    (entry.Critical ? criticalProblems : otherProblems).Add("Не совпадает SHA-256: " + entry.Path);
            }
        }

        foreach (var path in Directory.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories))
        {
            if (ShouldExclude(normalizedRoot, path)) continue;
            var relative = Path.GetRelativePath(normalizedRoot, path).Replace('\\', '/');
            if (expectedPaths.Contains(relative)) continue;
            if (IsCriticalPath(relative)) criticalProblems.Add("Неучтённый исполняемый файл: " + relative);
            else if (full) otherProblems.Add("Неучтённый файл: " + relative);
        }

        if (criticalProblems.Count > 0)
            return new IntegrityResult { State = IntegrityState.CriticalMismatch, Problems = criticalProblems.Concat(otherProblems).ToList() };
        if (otherProblems.Count > 0)
            return new IntegrityResult { State = IntegrityState.NonCriticalMismatch, Problems = otherProblems };
        return new IntegrityResult { State = IntegrityState.Verified };
    }

    public static void CreateManifest(string root, string? privateKeyPath)
    {
        var normalizedRoot = Path.GetFullPath(root);
        var version = FileVersionInfo.GetVersionInfo(Path.Combine(normalizedRoot, "NexusMonach.Browser.exe")).ProductVersion ?? "unknown";
        var manifest = new IntegrityManifest
        {
            Version = version,
            CreatedUtc = DateTimeOffset.UtcNow,
            Files = Directory.EnumerateFiles(normalizedRoot, "*", SearchOption.AllDirectories)
                .Where(path => !ShouldExclude(normalizedRoot, path))
                .Select(path => CreateEntry(normalizedRoot, path))
                .OrderBy(x => x.Path, StringComparer.Ordinal)
                .ToList()
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
        File.WriteAllBytes(Path.Combine(normalizedRoot, ManifestName), bytes);

        var signaturePath = Path.Combine(normalizedRoot, SignatureName);
        if (string.IsNullOrWhiteSpace(privateKeyPath))
        {
            if (File.Exists(signaturePath)) File.Delete(signaturePath);
            return;
        }

        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        var signature = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        File.WriteAllText(signaturePath, Convert.ToBase64String(signature), new UTF8Encoding(false));
    }

    public static void GenerateKeyPair(string directory)
    {
        Directory.CreateDirectory(directory);
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        File.WriteAllText(Path.Combine(directory, "integrity-private-key.pem"), ecdsa.ExportPkcs8PrivateKeyPem(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, PublicKeyName), ecdsa.ExportSubjectPublicKeyInfoPem(), new UTF8Encoding(false));
    }

    private static IntegrityFile CreateEntry(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        var info = new FileInfo(path);
        var aiPayload = relative.StartsWith("AI/models/", StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith("AI/node/node_modules/", StringComparison.OrdinalIgnoreCase);
        var critical = IsCriticalPath(relative);
        return new IntegrityFile
        {
            Path = relative,
            Length = info.Length,
            Sha256 = ComputeSha256(path),
            Critical = critical,
            Large = info.Length >= 64L * 1024 * 1024 || aiPayload
        };
    }

    private static bool ShouldExclude(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        return relative.Equals(ManifestName, StringComparison.OrdinalIgnoreCase) ||
               relative.Equals(SignatureName, StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) ||
               relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsCriticalPath(string relative)
    {
        var aiPayload = relative.StartsWith("AI/models/", StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith("AI/node/node_modules/", StringComparison.OrdinalIgnoreCase);
        return !aiPayload && (
            relative.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            relative.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            relative.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            relative.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase) ||
            relative.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase));
    }

    private static string ComputeSha256(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 1024 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static string ReadTrustedPublicKey(string fallbackPath)
    {
#if GUARDIAN_OFFICIAL
        using var embedded = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("Nexus.Guardian.integrity-public-key.pem");
        if (embedded is null)
            throw new InvalidDataException("В официальном Guardian отсутствует встроенный открытый ключ.");
        using var reader = new StreamReader(embedded, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
#else
        // Development packages are signed with a per-workstation key. Keep the
        // development and official trust boundaries strictly separated.
        return File.ReadAllText(fallbackPath);
#endif
    }

    private static bool HasEmbeddedPublicKey()
    {
#if GUARDIAN_OFFICIAL
        return Assembly.GetExecutingAssembly()
            .GetManifestResourceInfo("Nexus.Guardian.integrity-public-key.pem") is not null;
#else
        return false;
#endif
    }
}
