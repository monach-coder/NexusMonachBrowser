using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using NexusMonach.Models;

namespace NexusMonach.Services;

public static class ExtensionService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static List<ExtensionRecord> _records = [];
    private static readonly HashSet<string> EnsuredProfiles = new(StringComparer.OrdinalIgnoreCase);

    public static async Task InitializeAsync() =>
        _records = await JsonStore.ReadAsync<List<ExtensionRecord>>(AppPaths.ExtensionRegistryFile) ?? [];

    public static ExtensionRecord? Find(string id) => _records.FirstOrDefault(x => x.Id == id);

    public static async Task EnsureInstalledAsync(CoreWebView2Profile profile)
    {
        if (!EnsuredProfiles.Add(profile.ProfilePath))
            return;

        await Gate.WaitAsync();
        try
        {
            var installed = await profile.GetBrowserExtensionsAsync();
            // Remove the retired built-in DevTools AI extension from profiles
            // that used an earlier Nexus release. User-installed extensions are
            // identified by the managed registry and remain untouched.
            foreach (var retired in installed.Where(x =>
                         x.Name.Equals("Nexus Monach DevTools AI", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                try { await retired.RemoveAsync(); }
                catch { /* Retired extension cleanup must not block browsing. */ }
            }
            installed = await profile.GetBrowserExtensionsAsync();
            if (!BrowserEnvironment.ExtensionsEnabledAtStartup)
            {
                foreach (var extension in installed.Where(x => _records.Any(r => r.Id == x.Id)).ToList())
                    await extension.RemoveAsync();
                return;
            }
            var changed = false;
            foreach (var record in _records.ToList())
            {
                if (installed.Any(x => x.Id == record.Id))
                    continue;

                var path = ResolveManagedPath(record.ManagedPath);
                if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, "manifest.json")))
                    continue;

                var extension = await profile.AddBrowserExtensionAsync(path) ??
                    throw new InvalidOperationException("WebView2 не вернул установленное расширение.");
                record.Id = extension.Id;
                record.Name = extension.Name;
                changed = true;
            }
            if (changed)
                await JsonStore.WriteAsync(AppPaths.ExtensionRegistryFile, _records);
        }
        finally { Gate.Release(); }
    }

    public static async Task<CoreWebView2BrowserExtension> InstallAsync(CoreWebView2Profile profile, string sourceFolder)
    {
        var manifestPath = Path.Combine(sourceFolder, "manifest.json");
        if (!File.Exists(manifestPath))
            throw new InvalidOperationException("В выбранной папке нет manifest.json.");

        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(manifestPath));
        var root = manifest.RootElement;
        var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? "extension" : "extension";
        var version = root.TryGetProperty("version", out var v) ? v.GetString() ?? "0" : "0";
        if (!root.TryGetProperty("manifest_version", out var mv) || mv.GetInt32() is not (2 or 3))
            throw new InvalidOperationException("Поддерживаются расширения Manifest V2 и Manifest V3.");

        var sourceFingerprint = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(Path.GetFullPath(sourceFolder).ToUpperInvariant())))[..16];
        var target = Path.Combine(AppPaths.Extensions, Sanitize(name) + "-" + sourceFingerprint);
        var sourceFullPath = Path.GetFullPath(sourceFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (Path.GetFullPath(target).StartsWith(sourceFullPath, StringComparison.OrdinalIgnoreCase) ||
            sourceFullPath.StartsWith(Path.GetFullPath(AppPaths.Extensions).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выберите исходную папку расширения вне каталога Data\\Extensions.");

        await Gate.WaitAsync();
        try
        {
            var existingRecord = _records.FirstOrDefault(x => x.SourceFingerprint == sourceFingerprint);
            if (existingRecord is not null)
            {
                var installed = (await profile.GetBrowserExtensionsAsync()).FirstOrDefault(x => x.Id == existingRecord.Id);
                if (installed is not null)
                    await installed.RemoveAsync();
                _records.Remove(existingRecord);
            }

            if (Directory.Exists(target))
                Directory.Delete(target, recursive: true);
            CopyDirectory(sourceFolder, target);

            try
            {
                var extension = await profile.AddBrowserExtensionAsync(target) ??
                    throw new InvalidOperationException("WebView2 не вернул установленное расширение.");
                _records.Add(new ExtensionRecord
                {
                    Id = extension.Id,
                    Name = extension.Name,
                    Version = version,
                    ManagedPath = Path.GetRelativePath(AppPaths.AppRoot, target),
                    SourceFingerprint = sourceFingerprint,
                    InstalledAtUtc = DateTime.UtcNow
                });
                await JsonStore.WriteAsync(AppPaths.ExtensionRegistryFile, _records);
                return extension;
            }
            catch
            {
                Directory.Delete(target, recursive: true);
                throw;
            }
        }
        finally { Gate.Release(); }
    }

    public static async Task RemoveAsync(CoreWebView2BrowserExtension extension)
    {
        await Gate.WaitAsync();
        try
        {
            await extension.RemoveAsync();
            var record = _records.FirstOrDefault(x => x.Id == extension.Id);
            if (record is not null)
            {
                _records.Remove(record);
                var managedPath = ResolveManagedPath(record.ManagedPath);
                if (managedPath.StartsWith(Path.GetFullPath(AppPaths.Extensions), StringComparison.OrdinalIgnoreCase) &&
                    Directory.Exists(managedPath))
                    Directory.Delete(managedPath, recursive: true);
                await JsonStore.WriteAsync(AppPaths.ExtensionRegistryFile, _records);
            }
        }
        finally { Gate.Release(); }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) == 0)
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }
        foreach (var directory in Directory.EnumerateDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                continue;
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(value.Select(c => invalid.Contains(c) ? '-' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? "extension" : cleaned[..Math.Min(cleaned.Length, 48)];
    }

    private static string ResolveManagedPath(string storedPath) => Path.IsPathRooted(storedPath)
        ? storedPath
        : Path.GetFullPath(Path.Combine(AppPaths.AppRoot, storedPath));
}
