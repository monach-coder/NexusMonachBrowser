using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace Nexus.Guardian;

/// <summary>
/// Guardian-owned updater. It downloads to a side directory while the browser
/// keeps running, verifies the release ECDSA manifest, and applies files only
/// after the browser and its launcher have exited.
/// </summary>
internal static class SilentUpdateCoordinator
{
    private const string ReleaseApi =
        "https://api.github.com/repos/monach-coder/NexusMonachBrowser/releases/latest";
    private const string ReleaseAssetName = "NexusMonach-Portable-win-x64.zip";
    private const long MaxArchiveBytes = 8L * 1024 * 1024 * 1024;
    private const long MaxExpandedBytes = 12L * 1024 * 1024 * 1024;
    private const int MaxArchiveEntries = 150_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static string PendingPath(string guardianRoot) =>
        Path.Combine(guardianRoot, "Updates", "pending-update.json");

    public static void StartBackgroundCheck(string applicationRoot, string guardianRoot)
    {
        try
        {
            Directory.CreateDirectory(Path.Combine(guardianRoot, "Updates"));
            var throttle = Path.Combine(guardianRoot, "Updates", "last-check.utc");
            if (File.Exists(throttle) &&
                File.GetLastWriteTimeUtc(throttle) > DateTime.UtcNow.AddHours(-6)) return;
            File.WriteAllText(throttle, DateTimeOffset.UtcNow.ToString("O"));

            var guardian = Path.Combine(applicationRoot, "NexusMonach.exe");
            var info = new ProcessStartInfo(guardian)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = applicationRoot
            };
            info.ArgumentList.Add("--background-update-check");
            info.ArgumentList.Add(applicationRoot);
            Process.Start(info)?.Dispose();
        }
        catch { /* Updates are retried later; browser startup must stay available. */ }
    }

    public static async Task<int> CheckAndStageAsync(string applicationRoot, string guardianRoot,
        CancellationToken cancellationToken = default)
    {
        applicationRoot = NormalizeDirectory(applicationRoot);
        if (TryReadPending(guardianRoot, out _)) return 0;

        using var handler = new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 8 };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(45) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("NexusGuardian", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue(
            "application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");

        using var releaseResponse = await client.GetAsync(ReleaseApi,
            HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        releaseResponse.EnsureSuccessStatusCode();
        EnsureHttps(releaseResponse.RequestMessage?.RequestUri);
        await using var releaseStream = await releaseResponse.Content.ReadAsStreamAsync(cancellationToken);
        using var release = await JsonDocument.ParseAsync(releaseStream,
            new JsonDocumentOptions { MaxDepth = 32 }, cancellationToken);
        var root = release.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
        if (!TryParseVersion(tag, out var available)) return 0;
        var currentText = FileVersionInfo.GetVersionInfo(
            Path.Combine(applicationRoot, "NexusMonach.Browser.exe")).ProductVersion ?? "0.0.0";
        if (!TryParseVersion(currentText, out var current) || available <= current) return 0;

        var asset = root.GetProperty("assets").EnumerateArray().FirstOrDefault(item =>
            item.TryGetProperty("name", out var name) &&
            ReleaseAssetName.Equals(name.GetString(), StringComparison.OrdinalIgnoreCase));
        if (asset.ValueKind == JsonValueKind.Undefined)
            throw new InvalidDataException("Официальный архив обновления отсутствует в release.");
        var downloadUrl = new Uri(asset.GetProperty("browser_download_url").GetString()
                                  ?? throw new InvalidDataException("У release нет адреса архива."));
        EnsureHttps(downloadUrl);
        var declaredSize = asset.TryGetProperty("size", out var sizeElement)
            ? sizeElement.GetInt64() : 0;
        if (declaredSize <= 0 || declaredSize > MaxArchiveBytes)
            throw new InvalidDataException("Недопустимый размер архива обновления.");
        var digest = asset.TryGetProperty("digest", out var digestElement)
            ? digestElement.GetString() : null;

        var updatesRoot = Path.Combine(guardianRoot, "Updates");
        Directory.CreateDirectory(updatesRoot);
        var safeVersion = available.ToString().Replace('.', '-');
        var archivePath = Path.Combine(updatesRoot, $"nexus-{safeVersion}.zip.part");
        await DownloadAsync(client, downloadUrl, archivePath, declaredSize, cancellationToken);
        if (!string.IsNullOrWhiteSpace(digest) && digest.StartsWith("sha256:",
                StringComparison.OrdinalIgnoreCase))
        {
            var expected = digest[7..];
            var actual = await ComputeSha256Async(archivePath, cancellationToken);
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new CryptographicException("SHA-256 загруженного архива не совпадает с release.");
        }

        var staging = Path.Combine(updatesRoot, "staged-" + safeVersion);
        if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        Directory.CreateDirectory(staging);
        ExtractArchiveSafely(archivePath, staging);
        var verification = IntegrityVerifier.Verify(staging, full: true);
        if (verification.State != IntegrityState.Verified)
            throw new CryptographicException("Guardian отклонил обновление: " +
                                             string.Join("; ", verification.Problems.Take(8)));

        WritePendingAtomically(guardianRoot, new PendingGuardianUpdate
        {
            Version = available.ToString(),
            StagingDirectory = staging,
            TargetDirectory = applicationRoot,
            CreatedUtc = DateTimeOffset.UtcNow
        });
        try { File.Delete(archivePath); } catch { }
        return 0;
    }

    public static bool TryLaunchPendingApply(string applicationRoot, string guardianRoot, bool relaunch)
    {
        try
        {
            applicationRoot = NormalizeDirectory(applicationRoot);
            if (!TryReadPending(guardianRoot, out var pending) || pending is null ||
                !NormalizeDirectory(pending.TargetDirectory).Equals(applicationRoot,
                    StringComparison.OrdinalIgnoreCase)) return false;
            var staging = NormalizeDirectory(pending.StagingDirectory);
            if (!IsChildOf(Path.Combine(guardianRoot, "Updates"), staging)) return false;
            if (IntegrityVerifier.Verify(staging, full: true).State != IntegrityState.Verified) return false;
            var stagedGuardian = Path.Combine(staging, "NexusMonach.exe");
            if (!File.Exists(stagedGuardian)) return false;

            var info = new ProcessStartInfo(stagedGuardian)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                WorkingDirectory = staging
            };
            info.ArgumentList.Add("--apply-pending-update");
            info.ArgumentList.Add(Environment.ProcessId.ToString());
            if (relaunch) info.ArgumentList.Add("--relaunch");
            Process.Start(info)?.Dispose();
            return true;
        }
        catch { return false; }
    }

    public static int ApplyPendingUpdate(string guardianRoot, int parentProcessId, bool relaunch)
    {
        if (!TryReadPending(guardianRoot, out var pending) || pending is null) return 2;
        var staging = NormalizeDirectory(pending.StagingDirectory);
        var target = NormalizeDirectory(pending.TargetDirectory);
        if (!NormalizeDirectory(AppContext.BaseDirectory).Equals(staging,
                StringComparison.OrdinalIgnoreCase) ||
            !IsChildOf(Path.Combine(guardianRoot, "Updates"), staging)) return 3;
        if (IntegrityVerifier.Verify(staging, full: true).State != IntegrityState.Verified) return 4;

        try
        {
            if (parentProcessId > 0)
                try { Process.GetProcessById(parentProcessId).WaitForExit(30_000); }
                catch (ArgumentException) { }

            ApplyVerifiedDirectory(staging, target);
            var result = IntegrityVerifier.Verify(target, full: true);
            if (result.State != IntegrityState.Verified)
                throw new CryptographicException("Проверка установленного обновления не прошла: " +
                                                 string.Join("; ", result.Problems.Take(8)));
            File.Delete(PendingPath(guardianRoot));
            if (relaunch)
                Process.Start(new ProcessStartInfo(Path.Combine(target, "NexusMonach.exe"))
                {
                    UseShellExecute = false,
                    WorkingDirectory = target
                })?.Dispose();
            return 0;
        }
        catch { return 5; }
    }

    internal static void ExtractArchiveSafely(string archivePath, string destination)
    {
        destination = NormalizeDirectory(destination);
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count > MaxArchiveEntries)
            throw new InvalidDataException("Слишком много файлов в архиве обновления.");
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            expanded = checked(expanded + entry.Length);
            if (expanded > MaxExpandedBytes)
                throw new InvalidDataException("Распакованный архив обновления слишком велик.");
            var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
            if (unixMode == 0xA000) throw new InvalidDataException("Символические ссылки запрещены.");
            var path = Path.GetFullPath(Path.Combine(destination,
                entry.FullName.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsChildOf(destination, path))
                throw new InvalidDataException("Архив обновления содержит выход за целевой каталог.");
            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                Directory.CreateDirectory(path);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: true);
        }
    }

    private static void ApplyVerifiedDirectory(string staging, string target)
    {
        Directory.CreateDirectory(target);
        var newManifest = ReadManifest(staging);
        IntegrityManifest? oldManifest = null;
        try { oldManifest = ReadManifest(target); } catch { }

        var entries = newManifest.Files.OrderBy(entry =>
            entry.Path.Equals("NexusMonach.exe", StringComparison.OrdinalIgnoreCase) ? 1 : 0).ToArray();
        foreach (var entry in entries)
            CopyAtomically(Path.Combine(staging, entry.Path.Replace('/', Path.DirectorySeparatorChar)),
                Path.Combine(target, entry.Path.Replace('/', Path.DirectorySeparatorChar)));

        if (oldManifest is not null)
        {
            var current = newManifest.Files.Select(x => x.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var obsolete in oldManifest.Files.Where(x => !current.Contains(x.Path)))
            {
                var path = Path.GetFullPath(Path.Combine(target,
                    obsolete.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (IsChildOf(target, path) && File.Exists(path)) File.Delete(path);
            }
        }

        foreach (var control in new[]
                 {
                     IntegrityVerifier.ManifestName, IntegrityVerifier.SignatureName,
                     IntegrityVerifier.PublicKeyName, "portable.flag"
                 })
        {
            var source = Path.Combine(staging, control);
            if (File.Exists(source)) CopyAtomically(source, Path.Combine(target, control));
        }
    }

    private static void CopyAtomically(string source, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temporary = destination + ".nexus-new-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temporary, overwrite: true);
            File.Move(temporary, destination, overwrite: true);
        }
        finally { try { if (File.Exists(temporary)) File.Delete(temporary); } catch { } }
    }

    private static IntegrityManifest ReadManifest(string root) =>
        JsonSerializer.Deserialize<IntegrityManifest>(File.ReadAllBytes(
            Path.Combine(root, IntegrityVerifier.ManifestName)))
        ?? throw new InvalidDataException("Манифест обновления не читается.");

    private static async Task DownloadAsync(HttpClient client, Uri url, string destination,
        long declaredSize, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        EnsureHttps(response.RequestMessage?.RequestUri);
        var responseSize = response.Content.Headers.ContentLength;
        if (responseSize is > MaxArchiveBytes || responseSize is > 0 && responseSize != declaredSize)
            throw new InvalidDataException("Размер ответа обновления не совпадает с release.");
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write,
            FileShare.None, 1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        long total = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellationToken)) > 0)
        {
            total = checked(total + read);
            if (total > MaxArchiveBytes || total > declaredSize)
                throw new InvalidDataException("Архив обновления превысил заявленный размер.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
        if (total != declaredSize) throw new EndOfStreamException("Архив обновления загружен не полностью.");
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken)).ToLowerInvariant();
    }

    private static bool TryReadPending(string guardianRoot, out PendingGuardianUpdate? pending)
    {
        pending = null;
        try
        {
            var path = PendingPath(guardianRoot);
            if (!File.Exists(path)) return false;
            pending = JsonSerializer.Deserialize<PendingGuardianUpdate>(File.ReadAllText(path));
            return pending?.SchemaVersion == 1;
        }
        catch { return false; }
    }

    private static void WritePendingAtomically(string guardianRoot, PendingGuardianUpdate pending)
    {
        var path = PendingPath(guardianRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(pending, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    private static void EnsureHttps(Uri? uri)
    {
        if (uri is null || !uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Guardian запретил незашифрованный источник обновления.");
    }

    private static string NormalizeDirectory(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool IsChildOf(string parent, string candidate)
    {
        parent = NormalizeDirectory(parent) + Path.DirectorySeparatorChar;
        candidate = Path.GetFullPath(candidate);
        return candidate.StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParseVersion(string value, out Version version)
    {
        value = value.Trim().TrimStart('v', 'V');
        var numeric = new string(value.TakeWhile(ch => char.IsDigit(ch) || ch == '.').ToArray());
        return Version.TryParse(numeric, out version!);
    }
}
