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
using NexusMonach.Services.Diagnostics;

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
    private static readonly TimeSpan NonFatalDedupWindow = TimeSpan.FromSeconds(60);
    private static int _fatalRecorded;
    private static bool _initialized;
    private static DateTimeOffset? _lastNonFatalUtc;
    private static string? _lastNonFatalSignature;

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
        if (exception is null) return;
        // Startup may hit the same broken local worker twice in a few seconds
        // (chime plus the ready announcement, or a retrying lane). One sanitized
        // report per unique failure is enough for the vault; exact duplicates
        // only bury genuinely different problems behind repeated noise.
        var signature = string.Join('|',
            LimitToken(component), LimitToken(stage),
            exception.GetType().FullName ?? exception.GetType().Name, exception.Message);
        var now = DateTimeOffset.UtcNow;
        lock (FileGate)
        {
            if (_lastNonFatalSignature == signature &&
                _lastNonFatalUtc is { } previous && now - previous < NonFatalDedupWindow)
                return;
            _lastNonFatalSignature = signature;
            _lastNonFatalUtc = now;
        }
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
        // Перезапуск DWM (краш dwm.exe, смена темы, отключение монитора) на
        // секунды отключает композицию рабочего стола — WindowChrome не может
        // растянуть стеклянную рамку и бросает COMException. Это проходящее
        // состояние: DWM поднимется и отрисует заново. Гасим и работаем дальше
        // вместо убийства браузера.
        if (IsTransientDesktopCompositionFailure(e.Exception))
        {
            RecordNonFatal("wpf", "dwm-composition-restart", e.Exception);
            e.Handled = true;
            return;
        }
        // Гибель потока рендеринга WPF (UCEERR_RENDERTHREADFAILURE): окно уже
        // не отрисуется, но процесс жив. Молча закрываться нельзя — рапорт
        // пишем, озвучиваем причину и перезапускаемся через Guardian, который
        // по этому же рапорту поднимет безопасный режим (программная отрисовка
        // и WebView2 без GPU — лечит и зависания его рендерера).
        if (IsRenderThreadFailure(e.Exception))
        {
            // Если прямо сейчас шла аппаратная проба восстановления — сбой
            // означает «драйвер ещё не ожил»: сбрасываем счётчик и остаёмся
            // в осторожном режиме, без перезапуска.
            if (GpuRecoveryService.ProbeInProgress)
            {
                GpuRecoveryService.NotifyProbeRenderFailure();
                RecordNonFatal("gpu-recovery", "probe-render-failure", e.Exception);
                e.Handled = true;
                return;
            }
            if (!GuardianRuntime.IsSafeMode)
            {
                RecordNonFatal("wpf", "render-thread-failure", e.Exception);
                e.Handled = true;
                BeginGraphicsRecoveryRestart();
                return;
            }
        }
        RecordFatalCore(e.Exception, "wpf", "dispatcher-unhandled");
        e.Handled = true;
        Application.Current.Shutdown(-1);
    }

    /// <summary>0x80263001 — {Композиция рабочего стола отключена}.</summary>
    private static bool IsTransientDesktopCompositionFailure(Exception exception) =>
        exception is COMException com && unchecked((uint)com.HResult) == 0x80263001u;

    /// <summary>0x88980406 — UCEERR_RENDERTHREADFAILURE, поток отрисовки WPF умер.</summary>
    private static bool IsRenderThreadFailure(Exception exception) =>
        exception is COMException com &&
        (unchecked((uint)com.HResult) == 0x88980406u ||
         exception.Message.Contains("UCEERR_RENDERTHREADFAILURE", StringComparison.Ordinal));

    private static int _graphicsRestartStarted;

    /// <summary>
    /// Перезапуск после сбоя графики: голосом объясняем, что произошло,
    /// стартуем новый Guardian-процесс (он дождётся нашего выхода и поднимет
    /// браузер в безопасном режиме) и завершаемся.
    /// </summary>
    private static void BeginGraphicsRecoveryRestart()
    {
        if (Interlocked.Exchange(ref _graphicsRestartStarted, 1) != 0) return;
        try
        {
            VoiceAssistantService.Announce(
                "Внимание! Графический сбой окна. Перезапускаю браузер в безопасном режиме отрисовки.",
                VoiceAnnouncementPriority.Critical);
        }
        catch { /* Озвучка не должна мешать восстановлению. */ }
        _ = Task.Run(async () =>
        {
            // Даём фразе прозвучать до остановки голосовых сервисов в OnExit.
            await Task.Delay(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            try
            {
                var root = AppContext.BaseDirectory;
                var guardian = Path.Combine(root, "NexusMonach.exe");
                if (File.Exists(guardian))
                {
                    var info = new ProcessStartInfo(guardian)
                    {
                        UseShellExecute = false,
                        WorkingDirectory = root
                    };
                    info.ArgumentList.Add("--wait-for-previous-instance");
                    Process.Start(info);
                }
                else
                {
                    var browser = Environment.ProcessPath;
                    if (!string.IsNullOrWhiteSpace(browser))
                        Process.Start(new ProcessStartInfo(browser) { UseShellExecute = true });
                }
            }
            catch { /* Новый процесс не стартовал — завершаемся как обычно. */ }
            try { Application.Current?.Dispatcher.BeginInvoke(() => Application.Current.Shutdown(-1)); }
            catch { Environment.Exit(-1); }
        });
    }

    private static void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
            RecordFatalCore(exception, "runtime", "appdomain-unhandled");
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Сокеты ловушек и тарпита Дозора прерываются при остановке — это
        // плановый шум, а не сбой: не засоряем сейф отчётами о норме.
        if (IsPlannedSocketAbort(e.Exception))
        {
            e.SetObserved();
            return;
        }
        WriteReport(e.Exception, "tasks", "unobserved-task", fatal: false);
        e.SetObserved();
    }

    private static bool IsPlannedSocketAbort(Exception exception)
    {
        var candidates = exception is AggregateException aggregate
            ? aggregate.InnerExceptions
            : (IReadOnlyList<Exception>)[exception];
        return candidates.Count > 0 && candidates.All(IsSocketAbortCore);
    }

    private static bool IsSocketAbortCore(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Net.Sockets.SocketException socket)
                return socket.SocketErrorCode is
                    System.Net.Sockets.SocketError.OperationAborted or
                    System.Net.Sockets.SocketError.Interrupted or
                    System.Net.Sockets.SocketError.ConnectionAborted;
        }
        return false;
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

            // Причинный граф: хронология breadcrumbs + системные события
            // Windows + само исключение. Корневая причина видна сразу, а не
            // выискивается вручную по логам.
            var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
            var sanitizedMessage = SanitizeForReport(exception.Message);
            var systemEvents = SystemEventReader.ReadRecent(TimeSpan.FromMinutes(10));
            var causalGraph = CausalGraphBuilder.Build(new CausalGraphBuilder.CrashContext(
                exceptionType, sanitizedMessage, LimitToken(component), LimitToken(stage),
                Breadcrumbs.Select(b => (b.TimestampUtc, b.Component, b.Stage)).ToArray(),
                systemEvents));

            var report = new CrashReport
            {
                SchemaVersion = 2,
                Id = Guid.NewGuid().ToString("N"),
                TimestampUtc = DateTimeOffset.UtcNow,
                Fatal = fatal,
                BrowserVersion = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown",
                OsVersion = Environment.OSVersion.VersionString,
                WebView2Version = GetWebView2Version(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Component = LimitToken(component),
                Stage = LimitToken(stage),
                ExceptionType = exceptionType,
                Message = sanitizedMessage,
                StackTrace = FormatExceptionForReport(exception),
                IntegrityStatus = GuardianRuntime.IntegrityStatus,
                SafeMode = GuardianRuntime.IsSafeMode,
                GuardianSession = GuardianRuntime.SessionId,
                Breadcrumbs = Breadcrumbs.ToArray(),
                CausalGraph = causalGraph
            };
            var basePath = Path.Combine(VaultPath, $"{report.TimestampUtc:yyyyMMdd-HHmmss}-{report.Id}");
            lock (FileGate)
            {
                File.WriteAllText(basePath + ".pending.json", JsonSerializer.Serialize(report, JsonOptions));
                // Стандартные выгрузки графа: Mermaid для issue, DOT для Graphviz,
                // GraphML для Gephi. Падение экспорта не мешает основному рапорту.
                TryWriteGraphArtifacts(basePath, causalGraph);
            }
        }
        catch { /* Обработчик аварии не должен вызвать второе падение. */ }
    }

    private static void TryWriteGraphArtifacts(string basePath, CausalGraph causalGraph)
    {
        try
        {
            File.WriteAllText(basePath + ".pending.mermaid", CausalGraphExporter.ToMermaid(causalGraph));
            File.WriteAllText(basePath + ".pending.dot", CausalGraphExporter.ToDot(causalGraph));
            File.WriteAllText(basePath + ".pending.graphml", CausalGraphExporter.ToGraphML(causalGraph));
        }
        catch { /* Графовые выгрузки необязательны. */ }
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

    internal static string SanitizeForReport(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        var sanitized = UrlRegex().Replace(value, "[url-redacted]");
        sanitized = EmailRegex().Replace(sanitized, "[email-redacted]");
        sanitized = TokenRegex().Replace(sanitized, "$1=[secret-redacted]");
        sanitized = BearerRegex().Replace(sanitized, "Bearer [secret-redacted]");
        sanitized = JwtRegex().Replace(sanitized, "[token-redacted]");
        sanitized = WindowsPathRegex().Replace(sanitized, "[path-redacted]");
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(profile))
            sanitized = sanitized.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        return sanitized.Length > 16_000 ? sanitized[..16_000] : sanitized;
    }

    internal static string FormatExceptionForReport(Exception exception) =>
        SanitizeForReport(exception.ToString());

    [GeneratedRegex("https?://[^\\s\\\"'<>]+", RegexOptions.IgnoreCase)]
    private static partial Regex UrlRegex();

    [GeneratedRegex(@"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", RegexOptions.IgnoreCase)]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"(?i)\b(token|password|passwd|secret|authorization|cookie|cookies|set-cookie)\s*[:=]\s*(?:bearer\s+)?[^\s,;]+")]
    private static partial Regex TokenRegex();

    [GeneratedRegex(@"(?i)\bbearer\s+[A-Z0-9._~+/=-]{8,}")]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"\beyJ[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\.[A-Za-z0-9_-]{8,}\b")]
    private static partial Regex JwtRegex();

    [GeneratedRegex(@"(?i)\b[A-Z]:\\[^\r\n:'\x22]+")]
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
        public CausalGraph? CausalGraph { get; set; }
    }

    private sealed record CrashBreadcrumb(DateTimeOffset TimestampUtc, string Component, string Stage);
}
