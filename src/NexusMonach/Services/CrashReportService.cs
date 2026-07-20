using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using NexusMonach.Models;

namespace NexusMonach.Services;

public sealed class GuardianReportSnapshot
{
    public string FilePath { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; }
    public bool Fatal { get; init; }
    public bool Sent { get; init; }
    public string BrowserVersion { get; init; } = string.Empty;
    public string Component { get; init; } = string.Empty;
    public string Stage { get; init; } = string.Empty;
    public string ExceptionType { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string IntegrityStatus { get; init; } = string.Empty;
    public bool SafeMode { get; init; }
    public string Json { get; init; } = string.Empty;

    public string Title => $"{(Fatal ? "Аварийное завершение" : "Программная ошибка")} · {TimestampUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss}";
    public string Summary => $"{Component} / {Stage} · {(Sent ? "отправлен" : "только локально")}";
    public string Details =>
        $"Время: {TimestampUtc.ToLocalTime():dd.MM.yyyy HH:mm:ss zzz}\n" +
        $"Состояние: {(Sent ? "отправлен" : "хранится локально")}\n" +
        $"Тип: {(Fatal ? "аварийное завершение" : "нефатальная ошибка")}\n" +
        $"ID: {Id}\nВерсия браузера: {BrowserVersion}\n" +
        $"Компонент: {Component}\nЭтап: {Stage}\nИсключение: {ExceptionType}\n" +
        $"Целостность: {IntegrityStatus}\nБезопасный режим: {(SafeMode ? "да" : "нет")}\n\n" +
        $"Сообщение:\n{Message}\n\nОчищенный JSON:\n{Json}";
}

public static partial class CrashReportService
{
    private static readonly object FileGate = new();
    private static readonly ConcurrentQueue<CrashBreadcrumb> Breadcrumbs = new();
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static int _fatalRecorded;
    private static bool _initialized;

    public static string VaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NexusMonach", "Guardian", "CrashVault");

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        Directory.CreateDirectory(VaultPath);
        AddBreadcrumb("startup", "crash-handlers-ready");
        Application.Current.DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        WriteSessionResult(cleanExit: false);
    }

    public static void AddBreadcrumb(string component, string stage)
    {
        Breadcrumbs.Enqueue(new CrashBreadcrumb(DateTimeOffset.UtcNow, LimitToken(component), LimitToken(stage)));
        while (Breadcrumbs.Count > 50) Breadcrumbs.TryDequeue(out _);
    }

    public static void RecordNonFatal(string component, string stage, Exception? exception = null)
    {
        AddBreadcrumb(component, stage);
        if (exception is not null)
            WriteReport(exception, component, stage, fatal: false);
    }

    public static void RecordFatal(Exception exception, string component, string stage) =>
        RecordFatalCore(exception, component, stage);

    public static void MarkCleanExit()
    {
        if (Volatile.Read(ref _fatalRecorded) != 0) return;
        AddBreadcrumb("shutdown", "clean-exit");
        WriteSessionResult(cleanExit: true);
    }

    public static int PendingCount
    {
        get
        {
            try { return Directory.EnumerateFiles(VaultPath, "*.pending.json").Count(); }
            catch { return 0; }
        }
    }

    public static IReadOnlyList<GuardianReportSnapshot> GetLocalReports()
    {
        try
        {
            Directory.CreateDirectory(VaultPath);
            return Directory.EnumerateFiles(VaultPath, "*.json", SearchOption.TopDirectoryOnly)
                .Where(IsVaultReportPath)
                .Select(TryReadSnapshot)
                .Where(x => x is not null)
                .Cast<GuardianReportSnapshot>()
                .OrderByDescending(x => x.TimestampUtc)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    public static void CreateDiagnosticTestReport() =>
        RecordNonFatal("guardian", "manual-diagnostic",
            new InvalidOperationException("Проверочный локальный рапорт Nexus Guardian. Это не сбой браузера."));

    public static bool DeleteLocalReport(string path)
    {
        if (!IsVaultReportPath(path)) return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool ExportLocalReport(string sourcePath, string destinationPath)
    {
        if (!IsVaultReportPath(sourcePath) || string.IsNullOrWhiteSpace(destinationPath)) return false;
        try
        {
            File.Copy(sourcePath, destinationPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool IsDeliveryConfigured
    {
        get
        {
            var settings = SettingsService.Current;
            return settings.CrashReportDestination == CrashReportDestination.MatrixDirect
                ? IsHttps(settings.MatrixHomeserver) && !string.IsNullOrWhiteSpace(settings.MatrixRoomId) &&
                  WindowsCredentialStore.HasMatrixAccessToken()
                : IsHttps(settings.CrashReportEndpoint);
        }
    }

    public static async Task<int> SendPendingAsync(bool userApproved, CancellationToken cancellationToken = default)
    {
        var settings = SettingsService.Current;
        if (settings.CrashReportMode == CrashReportMode.LocalOnly) return 0;
        if (settings.CrashReportMode == CrashReportMode.AskBeforeSending && !userApproved) return 0;
        Uri? endpoint = null;
        string? matrixToken = null;
        if (settings.CrashReportDestination == CrashReportDestination.MatrixDirect)
        {
            if (!IsHttps(settings.MatrixHomeserver) || string.IsNullOrWhiteSpace(settings.MatrixRoomId)) return 0;
            matrixToken = WindowsCredentialStore.ReadMatrixAccessToken();
            if (string.IsNullOrWhiteSpace(matrixToken)) return 0;
        }
        else if (!Uri.TryCreate(settings.CrashReportEndpoint, UriKind.Absolute, out endpoint) ||
                 endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return 0;
        }

        var sent = 0;
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        foreach (var file in Directory.EnumerateFiles(VaultPath, "*.pending.json").Take(10))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var delivered = settings.CrashReportDestination == CrashReportDestination.MatrixDirect
                    ? await MatrixCrashReportTransport.SendReportAsync(client, settings.MatrixHomeserver,
                        settings.MatrixRoomId, matrixToken!, file, cancellationToken)
                    : await PostToCollectorAsync(client, endpoint!, file, cancellationToken);
                if (!delivered) continue;
                var sentPath = file.EndsWith(".pending.json", StringComparison.OrdinalIgnoreCase)
                    ? file[..^".pending.json".Length] + ".sent.json"
                    : file + ".sent.json";
                File.Move(file, sentPath, overwrite: true);
                sent++;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* Очередь остаётся локально для следующей попытки. */ }
        }
        return sent;
    }

    private static bool IsHttps(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps;

    private static GuardianReportSnapshot? TryReadSnapshot(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new GuardianReportSnapshot
            {
                FilePath = path,
                FileName = Path.GetFileName(path),
                Id = ReadString(root, "Id", "id"),
                TimestampUtc = ReadTimestamp(root, path),
                Fatal = ReadBoolean(root, "Fatal", "fatal"),
                Sent = path.EndsWith(".sent.json", StringComparison.OrdinalIgnoreCase),
                BrowserVersion = ReadString(root, "BrowserVersion", "browserVersion"),
                Component = ReadString(root, "Component", "component"),
                Stage = ReadString(root, "Stage", "stage"),
                ExceptionType = ReadString(root, "ExceptionType", "exceptionType"),
                Message = ReadString(root, "Message", "message"),
                IntegrityStatus = ReadString(root, "IntegrityStatus", "integrityStatus"),
                SafeMode = ReadBoolean(root, "SafeMode", "safeMode"),
                Json = json
            };
        }
        catch
        {
            return null;
        }
    }

    private static string ReadString(JsonElement root, string first, string second)
    {
        if (root.TryGetProperty(first, out var value) || root.TryGetProperty(second, out value))
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
        return string.Empty;
    }

    private static bool ReadBoolean(JsonElement root, string first, string second)
    {
        if (!(root.TryGetProperty(first, out var value) || root.TryGetProperty(second, out value))) return false;
        return value.ValueKind == JsonValueKind.True ||
               value.ValueKind == JsonValueKind.String && bool.TryParse(value.GetString(), out var result) && result;
    }

    private static DateTimeOffset ReadTimestamp(JsonElement root, string path)
    {
        var text = ReadString(root, "TimestampUtc", "timestampUtc");
        if (DateTimeOffset.TryParse(text, out var timestamp)) return timestamp;
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTimeOffset.UnixEpoch; }
    }

    private static bool IsVaultReportPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var root = Path.GetFullPath(VaultPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(path);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) &&
                   (full.EndsWith(".pending.json", StringComparison.OrdinalIgnoreCase) ||
                    full.EndsWith(".sent.json", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> PostToCollectorAsync(
        HttpClient client, Uri endpoint, string file, CancellationToken cancellationToken)
    {
        var ingestKey = GuardianReportingDefaults.IngestKey;
        if (!string.IsNullOrWhiteSpace(ingestKey))
            client.DefaultRequestHeaders.TryAddWithoutValidation("X-Nexus-Guardian-Key", ingestKey);
        await using var stream = File.OpenRead(file);
        using var content = new StreamContent(stream);
        content.Headers.ContentType = new("application/json");
        using var response = await client.PostAsync(endpoint, content, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    private static void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        RecordFatalCore(e.Exception, "wpf", "dispatcher-unhandled");
        e.Handled = true;
        Application.Current.Shutdown(-1);
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            RecordFatalCore(exception, "runtime", "appdomain-unhandled");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        WriteReport(e.Exception, "tasks", "unobserved-task", fatal: false);
        e.SetObserved();
    }

    private static void RecordFatalCore(Exception exception, string component, string stage)
    {
        if (Interlocked.Exchange(ref _fatalRecorded, 1) != 0) return;
        WriteReport(exception, component, stage, fatal: true);
        WriteSessionResult(cleanExit: false);
    }

    private static void WriteReport(Exception exception, string component, string stage, bool fatal)
    {
        try
        {
            Directory.CreateDirectory(VaultPath);
            var report = new CrashReport
            {
                SchemaVersion = 1,
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTimeOffset.UtcNow,
                Fatal = fatal,
                BrowserVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                OsVersion = Environment.OSVersion.VersionString,
                WebView2Version = GetWebView2Version(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Component = LimitToken(component),
                Stage = LimitToken(stage),
                ExceptionType = exception.GetType().FullName ?? exception.GetType().Name,
                Message = Sanitize(exception.Message),
                StackTrace = Sanitize(exception.StackTrace ?? string.Empty),
                IntegrityStatus = GuardianRuntime.IntegrityStatus,
                SafeMode = GuardianRuntime.IsSafeMode,
                GuardianSession = GuardianRuntime.SessionId,
                Breadcrumbs = Breadcrumbs.ToArray()
            };
            var path = Path.Combine(VaultPath, $"{report.TimestampUtc:yyyyMMdd-HHmmss}-{report.Id}.pending.json");
            lock (FileGate)
                File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOptions));
        }
        catch { /* Обработчик аварии не должен вызвать второе падение. */ }
    }

    private static void WriteSessionResult(bool cleanExit)
    {
        if (!GuardianRuntime.IsGuardianLaunch) return;
        try
        {
            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NexusMonach", "Guardian", "Sessions");
            Directory.CreateDirectory(directory);
            var payload = JsonSerializer.Serialize(new { sessionId = GuardianRuntime.SessionId, cleanExit });
            File.WriteAllText(Path.Combine(directory, GuardianRuntime.SessionId + ".json"), payload);
        }
        catch { }
    }

    private static string GetWebView2Version()
    {
        try { return CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { return "unavailable"; }
    }

    private static string LimitToken(string value)
    {
        var cleaned = Regex.Replace(value ?? string.Empty, "[^a-zA-Z0-9_.-]", "-");
        return cleaned[..Math.Min(64, cleaned.Length)];
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sanitized = UrlRegex().Replace(value, "[url-redacted]");
        sanitized = EmailRegex().Replace(sanitized, "[email-redacted]");
        sanitized = TokenRegex().Replace(sanitized, "$1=[secret-redacted]");
        sanitized = WindowsPathRegex().Replace(sanitized, "[path-redacted]");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            sanitized = sanitized.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return sanitized.Length > 16_000 ? sanitized[..16_000] : sanitized;
    }

    [GeneratedRegex("https?://[^\\s\\\"'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)\b(token|password|passwd|secret|authorization|cookie)\s*[:=]\s*[^\s,;]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?i)\b[A-Z]:\\[^\r\n:]+")]
    private static partial Regex WindowsPathRegex();

    private sealed class CrashReport
    {
        public int SchemaVersion { get; set; }
        public string Id { get; set; } = string.Empty;
        public DateTimeOffset TimestampUtc { get; set; }
        public bool Fatal { get; set; }
        public string BrowserVersion { get; set; } = string.Empty;
        public string OsVersion { get; set; } = string.Empty;
        public string WebView2Version { get; set; } = string.Empty;
        public string ProcessArchitecture { get; set; } = string.Empty;
        public string Component { get; set; } = string.Empty;
        public string Stage { get; set; } = string.Empty;
        public string ExceptionType { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string StackTrace { get; set; } = string.Empty;
        public string IntegrityStatus { get; set; } = string.Empty;
        public bool SafeMode { get; set; }
        public string GuardianSession { get; set; } = string.Empty;
        public IReadOnlyList<CrashBreadcrumb> Breadcrumbs { get; set; } = [];
    }

    private sealed record CrashBreadcrumb(DateTimeOffset TimestampUtc, string Component, string Stage);
}
