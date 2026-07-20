using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NexusMonach.Services;

public sealed class SecureRestartSession
{
    public int SchemaVersion { get; set; } = 1;
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public int ActiveIndex { get; set; }
    public List<SecureRestartTabState> Tabs { get; set; } = [];
}

public sealed class SecureRestartTabState
{
    public string Url { get; set; } = string.Empty;
    public double ScrollX { get; set; }
    public double ScrollY { get; set; }
    public List<SecureRestartFieldState> Fields { get; set; } = [];
}

public sealed class SecureRestartFieldState
{
    public string Selector { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public bool? Checked { get; set; }
}

/// <summary>
/// One-shot restart state protected by Windows DPAPI for the current user.
/// It never leaves the device and is deliberately separate from normal history.
/// </summary>
public static class SecureRestartSessionService
{
    private static readonly byte[] Entropy = SHA256.HashData(
        Encoding.UTF8.GetBytes("Nexus Monach Secure Restart Session v1"));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };
    private const int MaxProtectedBytes = 1024 * 1024;

    public static async Task SaveAsync(IReadOnlyList<SecureRestartTabState> tabs, int activeIndex)
    {
        var safeTabs = tabs
            .Where(tab => IsRestorableUrl(tab.Url))
            .Take(20)
            .Select(SanitizeTab)
            .ToList();
        if (safeTabs.Count == 0)
        {
            Delete();
            return;
        }

        var session = new SecureRestartSession
        {
            ActiveIndex = Math.Clamp(activeIndex, 0, safeTabs.Count - 1),
            SavedAtUtc = DateTime.UtcNow,
            Tabs = safeTabs
        };
        var plain = JsonSerializer.SerializeToUtf8Bytes(session, JsonOptions);
        var protectedBytes = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        if (protectedBytes.Length > MaxProtectedBytes)
            throw new InvalidOperationException("Зашифрованное состояние перезапуска превысило безопасный лимит.");

        Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SecureRestartSessionFile)!);
        var temporary = AppPaths.SecureRestartSessionFile + ".tmp";
        await File.WriteAllBytesAsync(temporary, protectedBytes);
        File.Move(temporary, AppPaths.SecureRestartSessionFile, overwrite: true);
    }

    public static async Task<SecureRestartSession?> LoadAsync()
    {
        if (!File.Exists(AppPaths.SecureRestartSessionFile)) return null;
        try
        {
            var protectedBytes = await File.ReadAllBytesAsync(AppPaths.SecureRestartSessionFile);
            if (protectedBytes.Length == 0 || protectedBytes.Length > MaxProtectedBytes)
                throw new InvalidDataException("Недопустимый размер состояния перезапуска.");
            var plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            var session = JsonSerializer.Deserialize<SecureRestartSession>(plain, JsonOptions);
            if (session is null || session.SchemaVersion != 1 ||
                DateTime.UtcNow - session.SavedAtUtc > TimeSpan.FromHours(2))
            {
                Delete();
                return null;
            }

            session.Tabs = session.Tabs
                .Where(tab => IsRestorableUrl(tab.Url))
                .Take(20)
                .Select(SanitizeTab)
                .ToList();
            if (session.Tabs.Count == 0)
            {
                Delete();
                return null;
            }
            session.ActiveIndex = Math.Clamp(session.ActiveIndex, 0, session.Tabs.Count - 1);
            return session;
        }
        catch (Exception ex) when (ex is CryptographicException or IOException or JsonException or InvalidDataException)
        {
            CrashReportService.RecordNonFatal("guardian", "secure-restart-session-read", ex);
            Delete();
            return null;
        }
    }

    public static void Delete()
    {
        try { if (File.Exists(AppPaths.SecureRestartSessionFile)) File.Delete(AppPaths.SecureRestartSessionFile); }
        catch { /* A stale encrypted file expires and is ignored on a later run. */ }
        try
        {
            var temporary = AppPaths.SecureRestartSessionFile + ".tmp";
            if (File.Exists(temporary)) File.Delete(temporary);
        }
        catch { }
    }

    public static SecureRestartTabState UrlOnly(string url) => new() { Url = url };

    private static SecureRestartTabState SanitizeTab(SecureRestartTabState source)
    {
        var totalCharacters = 0;
        var fields = new List<SecureRestartFieldState>();
        foreach (var field in source.Fields.Take(80))
        {
            if (string.IsNullOrWhiteSpace(field.Selector) || field.Selector.Length > 500 ||
                field.Kind is not ("text" or "checkbox" or "select" or "editable")) continue;
            var rawValue = field.Value ?? string.Empty;
            var value = rawValue[..Math.Min(rawValue.Length, 4000)];
            totalCharacters += value.Length;
            if (totalCharacters > 64 * 1024) break;
            fields.Add(new SecureRestartFieldState
            {
                Selector = field.Selector,
                Kind = field.Kind,
                Value = value,
                Checked = field.Checked
            });
        }
        return new SecureRestartTabState
        {
            Url = source.Url[..Math.Min(source.Url.Length, 2048)],
            ScrollX = Math.Clamp(source.ScrollX, 0, 10_000_000),
            ScrollY = Math.Clamp(source.ScrollY, 0, 10_000_000),
            Fields = fields
        };
    }

    private static bool IsRestorableUrl(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (UrlService.IsInternal(value)) return true;
        return Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";
    }
}
