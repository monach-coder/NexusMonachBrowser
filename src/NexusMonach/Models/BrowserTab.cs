using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NexusMonach.Services;
using NexusMonach.Views;

namespace NexusMonach.Models;

public sealed class UrlRequestedEventArgs(string value) : EventArgs
{
    public string Value { get; } = value;
    public bool OpenInNewTab { get; init; }
}

public sealed class SearchResultRequestedEventArgs(string query, string url) : EventArgs
{
    public string Query { get; } = query;
    public string Url { get; } = url;
}

public sealed record TabNetworkSnapshot(
    IReadOnlyList<string> ContactedHosts,
    IReadOnlyList<string> ThirdPartyHosts,
    IReadOnlyList<string> BlockedTrackerHosts,
    IReadOnlyList<NetworkRecipientSnapshot> Recipients,
    IReadOnlyList<int> ObservedPorts,
    int RequestCount,
    bool Truncated);

public sealed record NetworkRecipientSnapshot(
    string Host,
    int RequestCount,
    bool IsThirdParty,
    bool IsKnownTracker,
    bool WasBlocked,
    bool SentCookies,
    bool SentReferrer,
    bool SentOrigin,
    IReadOnlyList<string> ResourceKinds);

public sealed partial class BrowserTab : INotifyPropertyChanged, IDisposable
{
    private const int MaxObservedNetworkHosts = 512;
    private readonly bool _isPrivate;
    private readonly bool _navigateOnInitialize;
    private readonly TaskCompletionSource<bool> _firstNavigation =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _initializationTask;
    private string _title = "Новая вкладка";
    private string _address = string.Empty;
    private bool _isLoading;
    private int _blockedCount;
    private bool _disposed;
    private bool _isSuspended;
    private PhishingRiskLevel _phishingRisk;
    private string _securityWarning = string.Empty;
    private double _visualOpacity = 1;
    private readonly object _networkLock = new();
    private readonly HashSet<string> _contactedHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _thirdPartyHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _blockedTrackerHosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NetworkRecipientAccumulator> _networkRecipients =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<int> _observedPorts = [];
    private int _requestCount;
    private bool _networkSnapshotTruncated;
    private string _networkTopHost = string.Empty;
    private string _agentDomToken = string.Empty;
    private SecureRestartTabState? _pendingRestartState;
    private bool _restartStateRestoreRunning;
    private string? _pendingHttpFallback;
    private string? _upgradedHttpsUrl;
    private string? _httpAllowedOnce;

    public BrowserTab(string initialUrl, bool isPrivate, bool navigateOnInitialize = true)
    {
        InitialUrl = initialUrl;
        _isPrivate = isPrivate;
        _navigateOnInitialize = navigateOnInitialize;
        View = new WebView2
        {
            DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 11, 16, 24),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };
    }

    public string InitialUrl { get; }
    public WebView2 View { get; }
    public CoreWebView2? Core => View.CoreWebView2;
    public int WebViewProcessId => Core is { } core ? checked((int)core.BrowserProcessId) : 0;
    public bool IsInitialized => Core is not null;
    public bool IsPrivate => _isPrivate;
    public DateTime LastActivatedUtc { get; private set; } = DateTime.UtcNow;
    public bool IsSuspended => _isSuspended;
    public double VisualOpacity
    {
        get => _visualOpacity;
        private set { _visualOpacity = value; OnPropertyChanged(); }
    }

    public string Title
    {
        get => _title;
        private set { _title = value; OnPropertyChanged(); OnPropertyChanged(nameof(ShortTitle)); }
    }

    public string ShortTitle => Title.Length <= 26 ? Title : Title[..25] + "…";

    public string Address
    {
        get => _address;
        private set { _address = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set { _isLoading = value; OnPropertyChanged(); }
    }

    public int BlockedCount
    {
        get => _blockedCount;
        private set { _blockedCount = value; OnPropertyChanged(); }
    }

    public bool CanGoBack => Core?.CanGoBack == true;
    public bool CanGoForward => Core?.CanGoForward == true;
    public string CurrentUrl => Core?.Source ??
        (!string.IsNullOrWhiteSpace(Address) ? Address : InitialUrl);
    public string CurrentHost => Uri.TryCreate(CurrentUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;
    public bool IsSecureConnection => CurrentUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    public PhishingRiskLevel PhishingRisk
    {
        get => _phishingRisk;
        private set { _phishingRisk = value; OnPropertyChanged(); }
    }
    public string SecurityWarning
    {
        get => _securityWarning;
        private set { _securityWarning = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? StateChanged;
    public event EventHandler? NavigationSucceeded;
    public event EventHandler<UrlRequestedEventArgs>? OpenUrlRequested;
    public event EventHandler<UrlRequestedEventArgs>? NexusSearchRequested;
    public event EventHandler<SearchResultRequestedEventArgs>? SearchResultRequested;
    public event EventHandler? SettingsRequested;
    public event Action<string>? StatusMessageRequested;
    public Func<string, Task<CoreWebView2?>>? CreatePopupAsync { get; set; }

    public Task InitializeAsync() => _initializationTask ??= InitializeCoreAsync();

    public async Task WaitForFirstPageAsync(TimeSpan timeout)
    {
        await InitializeAsync();
        await Task.WhenAny(_firstNavigation.Task, Task.Delay(timeout));
    }

    public void Navigate(string url)
    {
        if (Core is null)
            return;
        Core.Settings.IsWebMessageEnabled = UrlService.IsInternal(url);
        Core.Navigate(url);
    }

    public void GoBack()
    {
        if (Core?.CanGoBack == true) Core.GoBack();
    }

    public void GoForward()
    {
        if (Core?.CanGoForward == true) Core.GoForward();
    }

    public void ReloadOrStop()
    {
        if (Core is null) return;
        if (IsLoading) Core.Stop(); else Core.Reload();
    }

    public void Reload() => Core?.Reload();

    public async Task ApplySettingsAsync()
    {
        if (Core is null) return;
        var settings = SettingsService.Current;
        Core.Settings.AreDevToolsEnabled = true;
        Core.Settings.IsPasswordAutosaveEnabled = !_isPrivate && settings.EnablePasswordAutosave;
        Core.Settings.IsGeneralAutofillEnabled = !_isPrivate && settings.EnableGeneralAutofill;
        BrowserEnvironment.ApplyPrivacyLevel(Core.Profile,
            _isPrivate ? PrivacyLevel.Strict : settings.PrivacyLevel);
        await ConfigureStartPageAsync();
    }

    private async Task ConfigureStartPageAsync()
    {
        if (Core is null || !CurrentUrl.Equals(UrlService.NewTabUrl, StringComparison.OrdinalIgnoreCase))
            return;

        var settings = SettingsService.Current;
        var network = Services.NetworkChainService.Snapshot();
        var configuration = JsonSerializer.Serialize(new
        {
            theme = settings.Theme.ToString(),
            mode = settings.ThemeMode.ToString(),
            network = JsonSerializer.Deserialize<JsonElement>(network.ToJson())
        });
        try
        {
            await Core.ExecuteScriptAsync($"window.nexusConfigureStartPage?.({configuration});");
        }
        catch (InvalidOperationException swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "ConfigureStartPageAsync", swallowed);
            // The tab may have navigated away while the theme was being applied.
        }
    }

    public void MarkActive()
    {
        LastActivatedUtc = DateTime.UtcNow;
        VisualOpacity = 1;
        if (_isSuspended && Core is not null)
        {
            Core.Resume();
            _isSuspended = false;
        }
    }

    public void UpdateVisualDecay(bool isActive, DateTime nowUtc)
    {
        if (isActive) { VisualOpacity = 1; return; }
        var idleMinutes = Math.Max(0, (nowUtc - LastActivatedUtc).TotalMinutes);
        if (idleMinutes < 5) { VisualOpacity = 1; return; }

        // После пяти минут вкладка мягко затухает до 42% за следующие 115 минут.
        VisualOpacity = Math.Clamp(1 - ((idleMinutes - 5) / 115 * 0.58), 0.42, 1);
    }

    public async Task TrySuspendAsync()
    {
        if (Core is null || _isSuspended || IsLoading || Core.IsDocumentPlayingAudio)
            return;
        try { _isSuspended = await Core.TrySuspendAsync(); }
        catch (Exception swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "TrySuspendAsync", swallowed);
            _isSuspended = false;
        }
    }

    public TabNetworkSnapshot GetNetworkSnapshot()
    {
        lock (_networkLock)
        {
            return new TabNetworkSnapshot(
                _contactedHosts.OrderBy(x => x).ToArray(),
                _thirdPartyHosts.OrderBy(x => x).ToArray(),
                _blockedTrackerHosts.OrderBy(x => x).ToArray(),
                _networkRecipients.Values
                    .OrderByDescending(x => x.IsThirdParty)
                    .ThenByDescending(x => x.IsKnownTracker)
                    .ThenBy(x => x.Host, StringComparer.OrdinalIgnoreCase)
                    .Select(x => x.Snapshot())
                    .ToArray(),
                _observedPorts.OrderBy(x => x).ToArray(),
                _requestCount,
                _networkSnapshotTruncated);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        View.Dispose();
    }

    private async Task InitializeCoreAsync()
    {
        if (_disposed) return;
        var controllerOptions = BrowserEnvironment.CreateControllerOptions(_isPrivate);
        await View.EnsureCoreWebView2Async(BrowserEnvironment.Current, controllerOptions);

        var core = Core!;
        BrowserEnvironment.RegisterProfile(core.Profile);
        BrowserEnvironment.ApplyPrivacyLevel(core.Profile,
            _isPrivate ? PrivacyLevel.Strict : SettingsService.Current.PrivacyLevel);
        if (!_isPrivate)
            await ExtensionService.EnsureInstalledAsync(core.Profile);

        // Режим Следа: farbling до создания документа — во всех фреймах
        // и до любых скриптов страницы.
        if (!_isPrivate && SettingsService.Current.TrailModeEnabled)
            await core.AddScriptToExecuteOnDocumentCreatedAsync(Services.FingerprintService.FarbleScript);

        // Мост аннотирования: панель над выделением, подсветки,
        // копирование в Markdown и захват видео-фрагментов.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(Services.AnnotationsBridge.Script);

        if (Directory.Exists(AppPaths.WebAssets))
        {
            core.SetVirtualHostNameToFolderMapping(
                "nexus.local",
                AppPaths.WebAssets,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        Address = _navigateOnInitialize ? UrlService.Resolve(InitialUrl) : "about:blank";
        ConfigureSettings(core.Settings, UrlService.IsInternal(Address));
        AttachEvents(core);
        TrackingProtectionService.Attach(core, () => core.Source, () =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                BlockedCount++;
                StateChanged?.Invoke(this, EventArgs.Empty);
            });
        }, RecordNetworkRequest, forceStrict: _isPrivate);

        if (_navigateOnInitialize)
            core.Navigate(Address);
    }

    private static string StripSensitiveUrl(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return value;
        return new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty }.Uri.AbsoluteUri;
    }

    private void ConfigureSettings(CoreWebView2Settings settings, bool allowLocalBridge)
    {
        var app = SettingsService.Current;
        settings.IsScriptEnabled = true;
        settings.AreDefaultScriptDialogsEnabled = true;
        settings.AreDefaultContextMenusEnabled = true;
        settings.AreBrowserAcceleratorKeysEnabled = true;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = allowLocalBridge;
        settings.AreDevToolsEnabled = true;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = true;
        settings.IsPinchZoomEnabled = true;
        settings.IsBuiltInErrorPageEnabled = true;
        settings.IsReputationCheckingRequired = true;
        settings.IsPasswordAutosaveEnabled = !_isPrivate && app.EnablePasswordAutosave;
        settings.IsGeneralAutofillEnabled = !_isPrivate && app.EnableGeneralAutofill;
        // User-Agent намеренно не меняется: стандартный Chromium-отпечаток менее уникален.
    }

    private bool HandleHttpsFirstNavigation(CoreWebView2 core, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!SettingsService.Current.HttpsFirstEnabled ||
            !Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp) return false;

        if (string.Equals(_httpAllowedOnce, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            _httpAllowedOnce = null;
            SecurityWarning = "Незашифрованное HTTP-соединение разрешено пользователем только для этого перехода.";
            return false;
        }

        // A server that redirects the HTTPS attempt back to the original HTTP
        // address must not create an endless upgrade loop.
        if (_upgradedHttpsUrl is not null &&
            string.Equals(_pendingHttpFallback, uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase))
        {
            _pendingHttpFallback = null;
            _upgradedHttpsUrl = null;
            e.Cancel = true;
            AskToOpenHttpOnce(core, uri.AbsoluteUri,
                "Сайт перенаправил защищённый HTTPS-переход обратно на незашифрованный HTTP.");
            return true;
        }

        e.Cancel = true;
        if (!NexusSearchNetworkGuard.TryParsePublicHttpUri(uri.AbsoluteUri, out _))
        {
            AskToOpenHttpOnce(core, uri.AbsoluteUri,
                "Адрес использует незашифрованный HTTP и не может быть безопасно обновлён автоматически " +
                "(локальный адрес, нестандартный порт или служебное имя).");
            return true;
        }

        var secure = new UriBuilder(uri)
        {
            Scheme = Uri.UriSchemeHttps,
            Port = -1
        }.Uri.AbsoluteUri;
        _pendingHttpFallback = uri.AbsoluteUri;
        _upgradedHttpsUrl = secure;
        SecurityWarning = "HTTPS-first обновил незашифрованный адрес до HTTPS.";
        Application.Current.Dispatcher.BeginInvoke(new Action(() => core.Navigate(secure)));
        return true;
    }

    private void AskToOpenHttpOnce(CoreWebView2 core, string url, string reason)
    {
        var owner = Window.GetWindow(View);
        var message = reason + "\n\nПродолжить только для этого перехода? " +
                      "Адрес и передаваемые данные смогут видеть или изменять участники сети.";
        var decision = owner is null
            ? GlassDialogWindow.Show(message, "Незашифрованное соединение", MessageBoxButton.YesNo,
                MessageBoxImage.Warning)
            : GlassDialogWindow.Show(owner, message, "Незашифрованное соединение", MessageBoxButton.YesNo,
                MessageBoxImage.Warning);
        if (decision != MessageBoxResult.Yes) return;
        _httpAllowedOnce = url;
        Application.Current.Dispatcher.BeginInvoke(new Action(() => core.Navigate(url)));
    }

    private void AttachEvents(CoreWebView2 core)
    {
        SelectedTextTranslationService.Attach(this, core, message => StatusMessageRequested?.Invoke(message));
        core.NavigationStarting += (_, e) =>
        {
            ResetNetworkSnapshot(e.Uri);
            core.Settings.IsWebMessageEnabled = UrlService.IsInternal(e.Uri);
            if (HandleHttpsFirstNavigation(core, e)) return;
            var phishing = PhishingProtectionService.Analyze(e.Uri);
            PhishingRisk = phishing.Level;
            SecurityWarning = phishing.Description;
            if (phishing.Level == PhishingRiskLevel.High)
            {
                e.Cancel = true;
                var owner = Window.GetWindow(View);
                // Query strings on sign-in pages can contain opaque session identifiers.
                // They are not useful for the decision and must never be copied into UI/logs.
                var warning = $"Возможная подмена адреса\n\n{phishing.Description}\n\nАдрес: {StripSensitiveUrl(e.Uri)}\n\nВсё равно открыть сайт?";
                var decision = owner is null
                    ? GlassDialogWindow.Show(warning, "Monach Anti-Phishing", MessageBoxButton.YesNo, MessageBoxImage.Stop)
                    : GlassDialogWindow.Show(owner, warning, "Monach Anti-Phishing", MessageBoxButton.YesNo, MessageBoxImage.Stop);
                if (decision == MessageBoxResult.Yes)
                {
                    PhishingProtectionService.TrustForSession(phishing.Host);
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => core.Navigate(e.Uri)));
                }
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }
            var cleaned = UrlService.CleanTrackingParameters(e.Uri, force: _isPrivate);
            if (!cleaned.Equals(e.Uri, StringComparison.Ordinal))
            {
                e.Cancel = true;
                Application.Current.Dispatcher.BeginInvoke(new Action(() => core.Navigate(cleaned)));
                return;
            }

            IsLoading = true;
            Address = e.Uri;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        core.SourceChanged += (_, _) =>
        {
            Address = core.Source;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        core.DocumentTitleChanged += (_, _) =>
        {
            Title = string.IsNullOrWhiteSpace(core.DocumentTitle) ? "Новая вкладка" : core.DocumentTitle;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };

        core.HistoryChanged += (_, _) => StateChanged?.Invoke(this, EventArgs.Empty);

        core.NavigationCompleted += (_, e) =>
        {
            IsLoading = false;
            Address = core.Source;
            _firstNavigation.TrySetResult(e.IsSuccess);
            StateChanged?.Invoke(this, EventArgs.Empty);
            if (e.IsSuccess)
            {
                _pendingHttpFallback = null;
                _upgradedHttpsUrl = null;
                NavigationSucceeded?.Invoke(this, EventArgs.Empty);
                _ = ConfigureStartPageAsync();
                _ = ApplySavedHighlightsAsync();
                _ = TryRestoreSecureRestartStateAsync();
            }
            else if (_pendingHttpFallback is not null && _upgradedHttpsUrl is not null)
            {
                var fallback = _pendingHttpFallback;
                _pendingHttpFallback = null;
                _upgradedHttpsUrl = null;
                var owner = Window.GetWindow(View);
                var message = "Защищённое HTTPS-соединение установить не удалось. " +
                              $"Ошибка WebView2: {e.WebErrorStatus}.\n\n" +
                              "Продолжить по незашифрованному HTTP? Адрес и передаваемые данные смогут видеть " +
                              "или изменять участники сети.";
                var decision = owner is null
                    ? GlassDialogWindow.Show(message, "HTTPS-first", MessageBoxButton.YesNo,
                        MessageBoxImage.Warning)
                    : GlassDialogWindow.Show(owner, message, "HTTPS-first", MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                if (decision == MessageBoxResult.Yes)
                {
                    _httpAllowedOnce = fallback;
                    Application.Current.Dispatcher.BeginInvoke(new Action(() => core.Navigate(fallback)));
                }
            }
        };

        core.NewWindowRequested += async (_, e) =>
        {
            e.Handled = true;
            var deferral = e.GetDeferral();
            try
            {
                if (CreatePopupAsync is not null)
                    e.NewWindow = await CreatePopupAsync(e.Uri);
            }
            finally
            {
                deferral.Complete();
            }
        };

        core.PermissionRequested += (_, e) => HandlePermission(e);
        core.WebMessageReceived += (_, e) => HandleWebMessage(e);
        core.DownloadStarting += (_, e) => HandleDownload(e);
        core.ProcessFailed += (_, e) =>
        {
            CrashReportService.RecordNonFatal("webview2", "process-" + e.ProcessFailedKind);
            Title = "Вкладка аварийно завершена";
            IsLoading = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        };
    }

    private void HandlePermission(CoreWebView2PermissionRequestedEventArgs e)
    {
        if (SettingsService.Current.BlockNotifications && e.PermissionKind == CoreWebView2PermissionKind.Notifications)
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            return;
        }

        var needsOurPrompt = e.PermissionKind is
            CoreWebView2PermissionKind.Camera or
            CoreWebView2PermissionKind.Microphone or
            CoreWebView2PermissionKind.Geolocation or
            CoreWebView2PermissionKind.ClipboardRead or
            CoreWebView2PermissionKind.FileReadWrite or
            CoreWebView2PermissionKind.OtherSensors or
            CoreWebView2PermissionKind.LocalFonts or
            CoreWebView2PermissionKind.MidiSystemExclusiveMessages or
            CoreWebView2PermissionKind.WindowManagement or
            CoreWebView2PermissionKind.MultipleAutomaticDownloads or
            CoreWebView2PermissionKind.Notifications;
        if (!needsOurPrompt)
            return;

        if (!TryGetExactWebOrigin(e.Uri, out var requestingOrigin) ||
            !TryGetExactWebOrigin(Core?.Source, out var topLevelOrigin) ||
            !OriginsEqual(requestingOrigin, topLevelOrigin) ||
            requestingOrigin.Scheme != Uri.UriSchemeHttps ||
            requestingOrigin.Host.Equals("nexus.local", StringComparison.OrdinalIgnoreCase))
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            return;
        }

        var origin = requestingOrigin.GetLeftPart(UriPartial.Authority);
        var permission = e.PermissionKind switch
        {
            CoreWebView2PermissionKind.Camera => "доступ к камере",
            CoreWebView2PermissionKind.Microphone => "доступ к микрофону",
            CoreWebView2PermissionKind.Geolocation => "местоположение",
            CoreWebView2PermissionKind.ClipboardRead => "чтение буфера обмена",
            CoreWebView2PermissionKind.Notifications => "уведомления",
            CoreWebView2PermissionKind.FileReadWrite => "чтение и изменение выбранных файлов",
            CoreWebView2PermissionKind.LocalFonts => "список локальных шрифтов",
            CoreWebView2PermissionKind.OtherSensors => "датчики устройства",
            CoreWebView2PermissionKind.MultipleAutomaticDownloads => "несколько автоматических загрузок",
            _ => e.PermissionKind.ToString()
        };

        var owner = Window.GetWindow(View);
        var result = owner is null
            ? GlassDialogWindow.Show($"Сайт {origin} запрашивает {permission}. Разрешить?", "Разрешение сайта",
                MessageBoxButton.YesNo, MessageBoxImage.Question)
            : GlassDialogWindow.Show(owner, $"Сайт {origin} запрашивает {permission}. Разрешить?", "Разрешение сайта",
                MessageBoxButton.YesNo, MessageBoxImage.Question);
        e.State = result == MessageBoxResult.Yes ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
        e.Handled = true;
    }

    private static bool TryGetExactWebOrigin(string? value, out Uri origin)
    {
        origin = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            uri.Scheme is not "http" and not "https" ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo))
            return false;

        origin = new UriBuilder(uri.Scheme, uri.IdnHost, uri.IsDefaultPort ? -1 : uri.Port).Uri;
        return true;
    }

    private static bool OriginsEqual(Uri left, Uri right) =>
        left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase) &&
        left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private void HandleWebMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        // Мост аннотирования приходит с любых страниц: сообщения nexus-*
        // валидируются отдельно (источник обязан совпадать с текущей вкладкой).
        if (HandleAnnotationMessage(e))
            return;

        if (!TryGetInternalMessagePage(e.Source, out var page))
            return;

        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!IsAllowedInternalMessage(page, type)) return;
            if (type == "navigate")
            {
                var value = root.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                    OpenUrlRequested?.Invoke(this, new UrlRequestedEventArgs(value) { OpenInNewTab = false });
            }
            else if (type == "search")
            {
                var value = root.TryGetProperty("value", out var v) ? v.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value))
                    NexusSearchRequested?.Invoke(this, new UrlRequestedEventArgs(value));
            }
            else if (type == "result-open")
            {
                var value = root.TryGetProperty("value", out var v) ? v.GetString() : null;
                var query = root.TryGetProperty("query", out var q) ? q.GetString() : null;
                if (!string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(query))
                    SearchResultRequested?.Invoke(this, new SearchResultRequestedEventArgs(query, value));
            }
            else if (type == "settings")
            {
                SettingsRequested?.Invoke(this, EventArgs.Empty);
            }
            else if (type is "toggle-vless" or "toggle-tor" or "toggle-proxy" or "toggle-warp"
                     or "toggle-auto")
            {
                // Тумблеры цепочки на стартовой странице: сервер, слой, прокси,
                // индикатор WARP, управляющий «Авто».
                _ = Task.Run(async () =>
                {
                    var snapshot = type switch
                    {
                        "toggle-vless" => await Services.NetworkChainService.ToggleVlessAsync(),
                        "toggle-tor" => await Services.NetworkChainService.ToggleTorAsync(),
                        "toggle-warp" => await Services.NetworkChainService.WarpButtonAsync(),
                        "toggle-auto" => await Services.NetworkChainService.ToggleAutoAsync(),
                        _ => await Services.NetworkChainService.ToggleProxyAsync()
                    };
                    await PushNetworkStateAsync(snapshot);
                });
            }
        }
        catch (JsonException)
        {
            // Сообщения неизвестного формата отбрасываются.
        }
    }

    /// <summary>
    /// Обрабатывает сообщения моста аннотирования (nexus-*): подсветка,
    /// заметка, копия в Markdown, захваченный видео-фрагмент. Источник
    /// сообщения обязан совпадать с адресом текущей вкладки.
    /// </summary>
    private bool HandleAnnotationMessage(CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson);
            var root = json.RootElement;
            if (!root.TryGetProperty("type", out var typeNode)) return false;
            var type = typeNode.GetString();
            if (type is null || !type.StartsWith("nexus-", StringComparison.Ordinal)) return false;

            // Источник — только текущая страница вкладки, никакие другие.
            var source = Uri.TryCreate(e.Source, UriKind.Absolute, out var src) ? src : null;
            if (source is null ||
                !string.Equals(CurrentUrl, source.ToString(), StringComparison.OrdinalIgnoreCase))
                return false;

            switch (type)
            {
                case "nexus-annotate":
                {
                    var quote = Trim(root, "quote", 8000);
                    if (quote.Length == 0) break;
                    var color = Enum.TryParse<Services.HighlightColor>(Trim(root, "color", 20), out var parsed)
                        ? parsed : Services.HighlightColor.Yellow;
                    Services.AnnotationsService.Add(new Services.PageAnnotation
                    {
                        Kind = Services.AnnotationKind.Highlight,
                        Quote = quote, Color = color,
                        Url = CurrentUrl, PageTitle = Title
                    });
                    break;
                }
                case "nexus-note":
                {
                    var quote = Trim(root, "quote", 8000);
                    var note = Trim(root, "note", 4000);
                    if (quote.Length == 0) break;
                    Services.AnnotationsService.Add(new Services.PageAnnotation
                    {
                        Kind = Services.AnnotationKind.Note,
                        Quote = quote, Note = note,
                        Url = CurrentUrl, PageTitle = Title
                    });
                    break;
                }
                case "nexus-copy-md":
                {
                    var markdown = Trim(root, "markdown", 100_000);
                    if (markdown.Length > 0)
                        System.Windows.Clipboard.SetText(markdown);
                    break;
                }
                case "nexus-video":
                {
                    _ = SaveVideoFragmentAsync(root);
                    break;
                }
                case "nexus-video-failed":
                    Services.CrashReportService.AddBreadcrumb("annotations",
                        "video-" + Trim(root, "reason", 40));
                    break;
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Trim(JsonElement root, string name, int limit)
    {
        if (!root.TryGetProperty(name, out var node)) return string.Empty;
        var value = node.GetString() ?? string.Empty;
        return value.Length <= limit ? value : value[..limit];
    }

    /// <summary>Сохраняет захваченный webm-фрагмент в Data/notes-media.</summary>
    private async Task SaveVideoFragmentAsync(JsonElement root)
    {
        try
        {
            var base64 = root.TryGetProperty("base64", out var data) ? data.GetString() : null;
            if (string.IsNullOrEmpty(base64) || base64.Length > 120_000_000) return;
            var position = root.TryGetProperty("position", out var pos) ? pos.GetDouble() : 0;
            var duration = root.TryGetProperty("duration", out var dur) ? dur.GetDouble() : 0;

            Directory.CreateDirectory(Services.AnnotationsService.MediaDirectory);
            var fileName = "fragment-" + DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss") + ".webm";
            await File.WriteAllBytesAsync(Path.Combine(Services.AnnotationsService.MediaDirectory, fileName),
                Convert.FromBase64String(base64));
            Services.AnnotationsService.Add(new Services.PageAnnotation
            {
                Kind = Services.AnnotationKind.VideoFragment,
                MediaPath = "notes-media/" + fileName,
                VideoPositionSeconds = position,
                DurationSeconds = duration,
                Url = CurrentUrl, PageTitle = Title
            });
        }
        catch (Exception ex)
        {
            Services.CrashReportService.RecordNonFatal("annotations", "video-save", ex);
        }
    }

    /// <summary>Подсвечивает сохранённые цитаты страницы после навигации.</summary>
    private async Task ApplySavedHighlightsAsync()
    {
        try
        {
            if (Core is null) return;
            var highlights = Services.AnnotationsService.ForUrl(CurrentUrl);
            if (highlights.Count == 0) return;
            await Core.ExecuteScriptAsync(Services.AnnotationsBridge.HighlightsScript(highlights));
        }
        catch (InvalidOperationException swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "ApplySavedHighlightsAsync", swallowed);
        }
    }

    /// <summary>Проталкивает состояние цепочки в открытую стартовую страницу.</summary>
    private async Task PushNetworkStateAsync(Services.NetworkChainSnapshot snapshot)    {
        try
        {
            if (Core is null || !CurrentUrl.Equals(UrlService.NewTabUrl, StringComparison.OrdinalIgnoreCase))
                return;
            await Core.ExecuteScriptAsync($"window.nexusNetworkState?.({snapshot.ToJson()});");
        }
        catch (InvalidOperationException swallowed)
        {
            Services.SwallowLog.Log("browser-tab", "PushNetworkStateAsync", swallowed);
        }
    }

    private static bool TryGetInternalMessagePage(string sourceValue, out string page)
    {
        page = string.Empty;
        if (!Uri.TryCreate(sourceValue, UriKind.Absolute, out var source) ||
            source.Scheme != Uri.UriSchemeHttps || !source.IsDefaultPort ||
            !string.IsNullOrEmpty(source.UserInfo) ||
            !source.Host.Equals("nexus.local", StringComparison.OrdinalIgnoreCase))
            return false;

        page = source.AbsolutePath;
        return page.Equals("/start.html", StringComparison.OrdinalIgnoreCase) ||
               page.Equals("/search.html", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAllowedInternalMessage(string page, string? type) =>
        page.Equals("/start.html", StringComparison.OrdinalIgnoreCase)
            ? type is "navigate" or "toggle-vless" or "toggle-tor" or "toggle-proxy" or "toggle-warp"
                or "toggle-auto"
            : page.Equals("/search.html", StringComparison.OrdinalIgnoreCase) && type == "result-open";

    private void HandleDownload(CoreWebView2DownloadStartingEventArgs e)
    {
        CrashReportService.AddBreadcrumb("downloads", "starting");
        var operation = e.DownloadOperation;
        var path = operation.ResultFilePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            Directory.CreateDirectory(downloads);
            path = MakeUniquePath(Path.Combine(downloads, "download.bin"));
            e.ResultFilePath = path;
        }

        var fileName = Path.GetFileName(path);
        var safeSource = DownloadSecurityService.SanitizeSourceForDisplay(operation.Uri);
        var assessment = DownloadSecurityService.Assess(fileName, safeSource);
        // Risk information is shown next to the file in the downloads flyout;
        // downloads are never interrupted by a modal warning. The local
        // Defender scan after completion is the active protection stage.

        // The built-in WebView2 download shelf stays open after completion
        // until dismissed; the Nexus indicator and hover flyout replace it.
        e.Handled = true;
        var item = new DownloadItem
        {
            FileName = Path.GetFileName(path),
            FilePath = path,
            SourceUrl = safeSource,
            BytesReceived = operation.BytesReceived,
            TotalBytes = NormalizeTotalBytes(operation.TotalBytesToReceive)
        };
        DownloadSecurityService.SetAssessment(item, assessment);
        DownloadService.Add(item);
        VoiceAssistantService.Announce("Загрузка началась.", VoiceAnnouncementPriority.Important, _isPrivate);
        var announcedMilestone = 0;

        operation.BytesReceivedChanged += (_, _) => Application.Current.Dispatcher.Invoke(() =>
        {
            item.BytesReceived = operation.BytesReceived;
            item.TotalBytes = NormalizeTotalBytes(operation.TotalBytesToReceive);
            if (item.TotalBytes > 0)
            {
                var percent = (int)Math.Clamp(item.BytesReceived * 100L / item.TotalBytes, 0, 100);
                var milestone = percent >= 75 ? 75 : percent >= 50 ? 50 : percent >= 25 ? 25 : 0;
                if (milestone > announcedMilestone)
                {
                    announcedMilestone = milestone;
                    VoiceAssistantService.Announce($"Загружено {milestone} процентов.",
                        VoiceAnnouncementPriority.Progress, _isPrivate);
                }
            }
        });
        operation.StateChanged += (_, _) => Application.Current.Dispatcher.Invoke(() =>
        {
            item.Status = operation.State switch
            {
                CoreWebView2DownloadState.Completed => "Завершено",
                CoreWebView2DownloadState.Interrupted => "Прервано: " + operation.InterruptReason,
                _ => "Загрузка"
            };
            if (operation.State == CoreWebView2DownloadState.Completed)
            {
                CrashReportService.AddBreadcrumb("downloads", "completed");
                _ = InspectAndScanAsync(item);
                VoiceAssistantService.Announce("Загрузка завершена. Файл проверяется локально.",
                    VoiceAnnouncementPriority.Important, _isPrivate);
            }
            else if (operation.State == CoreWebView2DownloadState.Interrupted)
            {
                CrashReportService.AddBreadcrumb("downloads", "interrupted");
                VoiceAssistantService.Announce("Загрузка прервана.", VoiceAnnouncementPriority.Critical, _isPrivate);
            }
        });
    }

    private static async Task InspectAndScanAsync(DownloadItem item)
    {
        try
        {
            await DownloadSecurityService.InspectCompletedAsync(item);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("downloads", "inspect-completed", ex);
        }
        await AntivirusScanService.ScanAsync(item);
    }

    private static string MakeUniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var folder = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 1; i < 10000; i++)
        {
            var candidate = Path.Combine(folder, $"{name} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
        return Path.Combine(folder, $"{name}-{Guid.NewGuid():N}{extension}");
    }

    private static long NormalizeTotalBytes(ulong? value)
    {
        if (!value.HasValue) return 0;
        return value.Value > long.MaxValue ? long.MaxValue : (long)value.Value;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
