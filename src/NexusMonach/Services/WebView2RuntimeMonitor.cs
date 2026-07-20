using Microsoft.Web.WebView2.Core;

namespace NexusMonach.Services;

public enum WebView2RuntimeState
{
    Current,
    RestartRequired,
    Missing,
    Unknown
}

public sealed class WebView2RuntimeSnapshot : EventArgs
{
    public WebView2RuntimeSnapshot(
        WebView2RuntimeState state,
        string activeVersion,
        string installedVersion,
        string sdkVersion,
        DateTimeOffset checkedAt,
        string message)
    {
        State = state;
        ActiveVersion = activeVersion;
        InstalledVersion = installedVersion;
        SdkVersion = sdkVersion;
        CheckedAt = checkedAt;
        Message = message;
    }

    public WebView2RuntimeState State { get; }
    public string ActiveVersion { get; }
    public string InstalledVersion { get; }
    public string SdkVersion { get; }
    public DateTimeOffset CheckedAt { get; }
    public string Message { get; }
}

/// <summary>
/// Локально наблюдает за Evergreen WebView2 Runtime. Сервис не скачивает и не
/// устанавливает обновления: этим занимается Microsoft Edge Update. Guardian
/// только сообщает, когда уже установленное ядро новее активного процесса и
/// начнёт использоваться после перезапуска браузера.
/// </summary>
public static class WebView2RuntimeMonitor
{
    private static readonly object Gate = new();
    private static CoreWebView2Environment? _environment;
    private static WebView2RuntimeSnapshot? _lastSnapshot;

    public static event EventHandler<WebView2RuntimeSnapshot>? StatusChanged;

    public static WebView2RuntimeSnapshot LastSnapshot
    {
        get
        {
            lock (Gate)
                return _lastSnapshot ?? Check();
        }
    }

    public static void Observe(CoreWebView2Environment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        lock (Gate)
        {
            if (ReferenceEquals(_environment, environment)) return;
            if (_environment is not null)
                _environment.NewBrowserVersionAvailable -= Environment_NewBrowserVersionAvailable;
            _environment = environment;
            _environment.NewBrowserVersionAvailable += Environment_NewBrowserVersionAvailable;
        }

        Publish(Check());
    }

    public static WebView2RuntimeSnapshot Check()
    {
        var checkedAt = DateTimeOffset.Now;
        var sdkVersion = typeof(CoreWebView2Environment).Assembly.GetName().Version?.ToString()
                         ?? "не определена";
        string activeVersion;
        lock (Gate)
            activeVersion = _environment?.BrowserVersionString ?? "не запущено";

        try
        {
            var installedVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            var active = ParseVersion(activeVersion);
            var installed = ParseVersion(installedVersion);
            var restartRequired = active is not null && installed is not null && installed > active;
            var snapshot = restartRequired
                ? new WebView2RuntimeSnapshot(
                    WebView2RuntimeState.RestartRequired,
                    activeVersion,
                    installedVersion,
                    sdkVersion,
                    checkedAt,
                    "Microsoft уже установила новое ядро. Закройте все окна Nexus Monach и запустите браузер снова.")
                : new WebView2RuntimeSnapshot(
                    WebView2RuntimeState.Current,
                    activeVersion,
                    installedVersion,
                    sdkVersion,
                    checkedAt,
                    "Используется установленное Evergreen-ядро. Обновления применяются после перезапуска браузера.");
            lock (Gate) _lastSnapshot = snapshot;
            return snapshot;
        }
        catch (WebView2RuntimeNotFoundException)
        {
            var snapshot = new WebView2RuntimeSnapshot(
                WebView2RuntimeState.Missing,
                activeVersion,
                "не найдено",
                sdkVersion,
                checkedAt,
                "Microsoft Edge WebView2 Runtime не найден. Браузеру требуется официальный Evergreen Runtime.");
            lock (Gate) _lastSnapshot = snapshot;
            return snapshot;
        }
        catch (Exception ex)
        {
            var snapshot = new WebView2RuntimeSnapshot(
                WebView2RuntimeState.Unknown,
                activeVersion,
                "проверка недоступна",
                sdkVersion,
                checkedAt,
                "Не удалось проверить локальную версию ядра: " + ex.Message);
            lock (Gate) _lastSnapshot = snapshot;
            return snapshot;
        }
    }

    private static void Environment_NewBrowserVersionAvailable(object? sender, object e) => Publish(Check());

    private static void Publish(WebView2RuntimeSnapshot snapshot)
    {
        lock (Gate) _lastSnapshot = snapshot;
        StatusChanged?.Invoke(null, snapshot);
    }

    private static Version? ParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var numeric = new string(value.TakeWhile(character => char.IsDigit(character) || character == '.').ToArray());
        return Version.TryParse(numeric, out var version) ? version : null;
    }
}
