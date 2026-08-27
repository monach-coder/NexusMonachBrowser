using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Nexus.Guardian;

/// <summary>
/// Подписанный манифест сетевой поставки: какие файлы скачивает лёгкий
/// установщик и фоновая подтяжка AI-моделей. Формат повторяет браузерный
/// ReleaseManifest (Services/OnlinePack) — схема простая и фиксируется
/// SchemaVersion=1.
/// </summary>
internal static class ReleaseManifestTool
{
    internal sealed class ManifestFile
    {
        public string RelativePath { get; set; } = string.Empty;
        public long Length { get; set; }
        public string Sha256 { get; set; } = string.Empty;
        public string Group { get; set; } = "ai";
        public string Purpose { get; set; } = string.Empty;
    }

    internal sealed class Manifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string Product { get; set; } = "Nexus Monach";
        public string Version { get; set; } = string.Empty;
        public DateTimeOffset CreatedUtc { get; set; }
        public List<ManifestFile> Files { get; set; } = [];
    }

    /// <summary>
    /// Пакеты сетевой поставки. Лимит GitHub Release — 2 ГиБ на файл,
    /// поэтому AI делится на смысловые части; каждая меньше ядра решения
    /// «браузер сначала, нейросети потом».
    /// </summary>
    private static readonly (string FileName, string Group, string Purpose, Func<string, bool> Contains)[] Packs =
    [
        ("nexus-core.zip", "core", "ядро браузера",
            relative => !relative.StartsWith("AI/", StringComparison.OrdinalIgnoreCase)),
        ("nexus-ai-runtime.zip", "ai", "среда нейросетей и голоса",
            relative => relative.StartsWith("AI/", StringComparison.OrdinalIgnoreCase) &&
                        relative.Split('/').Skip(1).First() is "node" or "node_modules" or "ffmpeg"
                            or "adapters" or "whisper" or "dictionaries" or "llama" or "voice"),
        ("nexus-ai-models.zip", "ai", "модели перевода и голоса",
            relative => relative.StartsWith("AI/models/", StringComparison.OrdinalIgnoreCase) &&
                        relative.Split('/').Skip(2).First() is "translation" or "voice" or "whisper"
                            or "multilingual-e5-small" or "parakeet-tdt"),
        ("nexus-ai-vlm.zip", "ai", "описание страниц (опционально)",
            relative => relative.StartsWith("AI/models/", StringComparison.OrdinalIgnoreCase) &&
                        relative.Split('/').Skip(2).First() is "qwen3-0.6b" or "smolvlm-500m"),
    ];

    /// <summary>
    /// Создаёт release-manifest.json (+ .sig при ключе) и пакеты поставки
    /// рядом со сборкой: core без AI — лёгкая установка, AI — тремя пакетами
    /// с докачкой и проверкой хеша.
    /// </summary>
    public static int Create(string root, string? privateKeyPath)
    {
        var normalized = Path.GetFullPath(root);
        var browser = Path.Combine(normalized, "NexusMonach.Browser.exe");
        if (!File.Exists(browser))
        {
            Console.Error.WriteLine("Не найден NexusMonach.Browser.exe в " + normalized);
            return 2;
        }
        var version = FileVersionInfo.GetVersionInfo(browser).ProductVersion ?? "unknown";
        var outputDirectory = Directory.GetParent(normalized)!.FullName;

        var files = new List<ManifestFile>();
        var manifestPrivateKeyResolved = privateKeyPath is not null && File.Exists(privateKeyPath)
            ? Path.GetFullPath(privateKeyPath)
            : null;
        foreach (var (fileName, group, purpose, contains) in Packs)
        {
            Console.WriteLine("Пакет " + fileName + " (" + purpose + ")…");
            var zipPath = Path.Combine(outputDirectory, fileName);
            if (File.Exists(zipPath)) File.Delete(zipPath);

            // Ядро едет с манифестом целостности, где AI помечен Pending:
            // лёгкая установка без AI проходит проверку, пакеты после доставки
            // проверяются теми же хешами.
            if (group == "core" && manifestPrivateKeyResolved is not null)
                IntegrityVerifier.CreateManifest(normalized, manifestPrivateKeyResolved, markAiPending: true);

            var entries = 0;
            using (var archive = System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                foreach (var path in Directory.EnumerateFiles(normalized, "*", SearchOption.AllDirectories))
                {
                    var relative = Path.GetRelativePath(normalized, path).Replace('\\', '/');
                    if (relative.StartsWith("Data/", StringComparison.OrdinalIgnoreCase) ||
                        relative.StartsWith(".mimosa", StringComparison.OrdinalIgnoreCase) ||
                        relative.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase) ||
                        relative.Equals("release-manifest.json", StringComparison.OrdinalIgnoreCase) ||
                        relative.Equals("release-manifest.json.sig", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!contains(relative)) continue;
                    archive.CreateEntryFromFile(path, relative,
                        System.IO.Compression.CompressionLevel.Fastest);
                    entries++;
                }
            }
            if (entries == 0)
            {
                File.Delete(zipPath);
                continue;
            }

            // Возврат обычного манифеста дистрибутиву после сборки ядра.
            if (group == "core" && manifestPrivateKeyResolved is not null)
                IntegrityVerifier.CreateManifest(normalized, manifestPrivateKeyResolved);

            files.Add(new ManifestFile
            {
                RelativePath = fileName,
                Length = new FileInfo(zipPath).Length,
                Sha256 = Sha256(zipPath),
                Group = group,
                Purpose = purpose
            });
            Console.WriteLine("  файлов: " + entries);
        }

        var manifest = new Manifest
        {
            Version = version,
            CreatedUtc = DateTimeOffset.UtcNow,
            Files = files.OrderBy(f => f.Group, StringComparer.Ordinal).ThenBy(f => f.RelativePath, StringComparer.Ordinal).ToList()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions { WriteIndented = true });
        var manifestPath = Path.Combine(normalized, "release-manifest.json");
        File.WriteAllBytes(manifestPath, bytes);
        Console.WriteLine("Манифест поставки: " + manifestPath + " (" + files.Count + " пакетов)");

        if (string.IsNullOrWhiteSpace(privateKeyPath))
        {
            var sigPath = manifestPath + ".sig";
            if (File.Exists(sigPath)) File.Delete(sigPath);
            return 0;
        }
        using var ecdsa = ECDsa.Create();
        ecdsa.ImportFromPem(File.ReadAllText(privateKeyPath));
        var signature = ecdsa.SignData(bytes, HashAlgorithmName.SHA256);
        File.WriteAllText(manifestPath + ".sig", Convert.ToBase64String(signature), new UTF8Encoding(false));
        Console.WriteLine("Подпись: " + manifestPath + ".sig");
        return 0;
    }

    /// <summary>Назначение файла поставки — основа голосового сообщения.</summary>
    internal static string AiPurpose(string relative)
    {
        if (relative.Contains("whisper", StringComparison.OrdinalIgnoreCase))
            return "распознавание речи whisper";
        if (relative.Contains("voice/silero", StringComparison.OrdinalIgnoreCase))
            return "голос silero";
        if (relative.Contains("piper", StringComparison.OrdinalIgnoreCase))
            return "голос piper";
        if (relative.Contains("translation", StringComparison.OrdinalIgnoreCase))
            return "перевод страниц";
        if (relative.Contains("e5", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("embedding", StringComparison.OrdinalIgnoreCase))
            return "семантический поиск";
        if (relative.Contains("stress", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("russian", StringComparison.OrdinalIgnoreCase))
            return "русские словари";
        if (relative.Contains("qwen", StringComparison.OrdinalIgnoreCase) ||
            relative.Contains("vlm", StringComparison.OrdinalIgnoreCase))
            return "описание страниц";
        return "модели";
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
}
