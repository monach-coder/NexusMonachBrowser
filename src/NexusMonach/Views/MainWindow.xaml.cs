using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NexusMonach.Models;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class MainWindow : Window
{
    private static readonly Lazy<Task> DataInitialization = new(async () =>
    {
        await BookmarkService.InitializeAsync();
        await ExtensionService.InitializeAsync();
        await SiteRuleService.InitializeAsync();
        await KnowledgeGraphService.InitializeAsync();
    });

    private readonly bool _isPrivate;
    private bool _closeReady;
    private bool _closing;
    private bool _initialized;
    private bool _restartRequested;
    private bool _coreUpdatePromptShown;
    private readonly DispatcherTimer _memoryTimer;
    private readonly DispatcherTimer _networkPerformanceTimer;
    private readonly Dictionary<string, (long Received, long Sent)> _networkCounters = new(StringComparer.Ordinal);
    private DateTime _networkSampleUtc = DateTime.UtcNow;
    private double _downloadBytesPerSecond;
    private double _uploadBytesPerSecond;
    private long? _pingMilliseconds;
    private int _networkTick;
    private bool _pingBusy;
    private DateTime _localPortsUpdatedUtc = DateTime.MinValue;
    private int[] _localTcpPorts = [];
    private int[] _localUdpPorts = [];
    private LocalPortInfo[] _localPortDetails = [];
    private bool _localPortsAvailable;
    private readonly Dictionary<BrowserTab, string> _lastKnowledgeUrl = [];
    private readonly Dictionary<BrowserTab, CancellationTokenSource> _searchOperations = [];
    private readonly Dictionary<BrowserTab, string> _pendingSearchFollowUp = [];
    private readonly Dictionary<BrowserTab, SiteResearchContext> _siteResearchContexts = [];
    private readonly Dictionary<BrowserTab, CancellationTokenSource> _siteResearchOperations = [];
    private static readonly JsonSerializerOptions WebJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public ObservableCollection<BrowserTab> Tabs { get; } = [];
    private BrowserTab? ActiveTab => TabsList.SelectedItem as BrowserTab;
    private sealed record SiteResearchContext(string Query, string Host);

    public MainWindow(bool isPrivate)
    {
        _isPrivate = isPrivate;
        InitializeComponent();
        CrashReportService.Initialize();
        DataContext = this;
        PrivateBadge.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;
        ExtensionsMenuItem.IsEnabled = !isPrivate && BrowserEnvironment.ExtensionsEnabledAtStartup;
        Title = isPrivate ? "Nexus Monach — приватное окно" : "Nexus Monach";
        UpdatePrivacyLabel();
        _memoryTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _memoryTimer.Tick += MemoryTimer_Tick;
        _networkPerformanceTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _networkPerformanceTimer.Tick += NetworkPerformanceTimer_Tick;
        _networkPerformanceTimer.Start();
        if (!isPrivate)
        {
            WebView2RuntimeMonitor.StatusChanged += WebView2RuntimeMonitor_StatusChanged;
            Closed += (_, _) => WebView2RuntimeMonitor.StatusChanged -= WebView2RuntimeMonitor_StatusChanged;
        }
        if (Environment.GetCommandLineArgs().Any(x =>
                x.Equals("--guardian-test-crash", StringComparison.OrdinalIgnoreCase)))
        {
            Dispatcher.BeginInvoke(new Action(() =>
                throw new InvalidOperationException("Intentional Nexus Guardian crash-pipeline test.")),
                DispatcherPriority.ApplicationIdle);
        }
    }

    public async Task InitializeAsync(bool waitForFirstPage)
    {
        if (_initialized) return;
        _initialized = true;
        await DataInitialization.Value;

        SecureRestartSession? secureRestartSession = null;
        BrowserSession? session = null;
        if (!_isPrivate)
        {
            secureRestartSession = await SecureRestartSessionService.LoadAsync();
            if (secureRestartSession is null)
                session = await SessionService.LoadAsync();
        }

        if (secureRestartSession is { Tabs.Count: > 0 })
        {
            foreach (var state in secureRestartSession.Tabs)
            {
                var tab = AddTab(state.Url);
                tab.SetPendingRestartState(state);
            }
            TabsList.SelectedIndex = secureRestartSession.ActiveIndex;
        }
        else if (session is { Urls.Count: > 0 })
        {
            foreach (var url in session.Urls)
                AddTab(url);
            TabsList.SelectedIndex = session.ActiveIndex;
        }
        else
        {
            var tab = AddTab(UrlService.NewTabUrl);
            TabsList.SelectedItem = tab;
        }

        if (ActiveTab is not null)
        {
            await EnsureTabReadyAsync(ActiveTab);
            if (waitForFirstPage)
                await ActiveTab.WaitForFirstPageAsync(TimeSpan.FromSeconds(30));
        }
        SyncUi();
        if (ActiveTab is not null && UrlService.IsInternal(ActiveTab.CurrentUrl))
        {
            AddressBox.Focus();
            AddressBox.SelectAll();
        }
        _memoryTimer.Start();
        if (!_isPrivate)
            await PrivacyDock.SetEnabledAsync(SettingsService.Current.ShowPrivacyMonitor);
        else
            PrivacyDock.Visibility = Visibility.Collapsed;
        if (secureRestartSession is not null)
            SecureRestartSessionService.Delete();
        if (!_isPrivate)
            HandleCoreUpdateSnapshot(WebView2RuntimeMonitor.Check());
    }

    private BrowserTab AddTab(string url, bool navigateOnInitialize = true, bool insertAfterActive = false)
    {
        var tab = new BrowserTab(url, _isPrivate, navigateOnInitialize);
        tab.StateChanged += (_, _) => Dispatcher.Invoke(SyncUi);
        tab.NavigationSucceeded += (_, _) =>
        {
            Dispatcher.Invoke(() => LocalAiDock.HandleNavigation(tab));
            if (!_isPrivate)
            {
                var current = tab.CurrentUrl;
                _lastKnowledgeUrl.TryGetValue(tab, out var previous);
                _lastKnowledgeUrl[tab] = current;
                _ = IndexKnowledgeAsync(tab, current, previous);
            }
            if (_pendingSearchFollowUp.TryGetValue(tab, out var searchQuery) &&
                !UrlService.IsInternal(tab.CurrentUrl) && !UrlService.IsSearchProviderUrl(tab.CurrentUrl))
            {
                _pendingSearchFollowUp.Remove(tab);
                _siteResearchContexts[tab] = new SiteResearchContext(searchQuery, tab.CurrentHost);
                ScheduleSelectedSiteResearch(tab, searchQuery);
            }
            else if (_siteResearchContexts.TryGetValue(tab, out var context))
            {
                if (IsSameSite(context.Host, tab.CurrentHost) &&
                    !UrlService.IsInternal(tab.CurrentUrl) && !UrlService.IsSearchProviderUrl(tab.CurrentUrl))
                    ScheduleSelectedSiteResearch(tab, context.Query);
                else
                    StopSelectedSiteResearch(tab, forgetContext: true);
            }
            Dispatcher.Invoke(SyncUi);
        };
        tab.OpenUrlRequested += async (_, e) =>
        {
            if (e.OpenInNewTab)
                await OpenTabAsync(e.Value);
            else
                NavigateActive(e.Value);
        };
        tab.NexusSearchRequested += async (_, e) => await RunNexusSearchAsync(tab, e.Value);
        tab.SearchResultRequested += async (_, e) =>
        {
            if (!NexusSearchService.IsAllowedResultUrl(e.Url) || !Uri.TryCreate(e.Url, UriKind.Absolute, out var target)) return;
            if (!_isPrivate) await NexusSearchService.RecordChoiceAsync(e.Query, target.AbsoluteUri);
            _pendingSearchFollowUp[tab] = e.Query;
            tab.Navigate(target.AbsoluteUri);
        };
        tab.SettingsRequested += (_, _) => Dispatcher.Invoke(ShowSettings);
        tab.StatusMessageRequested += message => Dispatcher.Invoke(() => NetworkStatusText.Text = message);
        tab.CreatePopupAsync = CreatePopupAsync;
        var insertIndex = insertAfterActive ? TabsList.SelectedIndex + 1 : Tabs.Count;
        if (insertIndex >= 0 && insertIndex < Tabs.Count)
            Tabs.Insert(insertIndex, tab);
        else
            Tabs.Add(tab);
        return tab;
    }

    private async Task<Microsoft.Web.WebView2.Core.CoreWebView2?> CreatePopupAsync(string requestedUrl)
    {
        var tab = AddTab("about:blank", navigateOnInitialize: false, insertAfterActive: true);
        TabsList.SelectedItem = tab;
        await EnsureTabReadyAsync(tab);
        return tab.Core;
    }

    private async Task OpenTabAsync(string? input = null)
    {
        var tab = AddTab(string.IsNullOrWhiteSpace(input) ? UrlService.NewTabUrl : UrlService.Resolve(input),
            insertAfterActive: true);
        TabsList.SelectedItem = tab;
        await EnsureTabReadyAsync(tab);
        AddressBox.Focus();
        if (UrlService.IsInternal(tab.CurrentUrl))
            AddressBox.SelectAll();
    }

    private async Task EnsureTabReadyAsync(BrowserTab tab)
    {
        BrowserHost.Content = tab.View;
        tab.MarkActive();
        if (!tab.IsInitialized)
        {
            TabLoadingOverlay.Visibility = Visibility.Visible;
            try { await tab.InitializeAsync(); }
            catch (Exception ex)
            {
                GlassDialogWindow.Show(this, "Не удалось открыть вкладку:\n\n" + ex.Message,
                    "Ошибка вкладки", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally { TabLoadingOverlay.Visibility = Visibility.Collapsed; }
        }
        SyncUi();
        LocalAiDock.UpdateTab(tab);
    }

    private void NavigateActive(string input)
    {
        if (ActiveTab?.Core is null) return;
        ActiveTab.Navigate(UrlService.Resolve(input));
        Keyboard.ClearFocus();
    }

    private Task NavigateOrSearchAsync(string input)
    {
        var tab = ActiveTab;
        if (tab?.Core is null) return Task.CompletedTask;
        Keyboard.ClearFocus();
        if (UrlService.IsSearchQuery(input))
        {
            var query = input.Trim();
            _pendingSearchFollowUp[tab] = query;
            tab.Navigate(UrlService.Resolve(query));
            NetworkStatusText.Text = "Поиск открыт · Следопыт включится после выбора сайта";
        }
        else
            tab.Navigate(UrlService.Resolve(input));
        return Task.CompletedTask;
    }

    private void ScheduleSelectedSiteResearch(BrowserTab tab, string query)
    {
        StopSelectedSiteResearch(tab, forgetContext: false);
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        _siteResearchOperations[tab] = cancellation;
        _ = AnalyzeSelectedSearchResultAsync(tab, query, tab.CurrentUrl, cancellation);
    }

    private void StopSelectedSiteResearch(BrowserTab tab, bool forgetContext)
    {
        if (_siteResearchOperations.Remove(tab, out var operation))
        {
            operation.Cancel();
        }
        if (forgetContext) _siteResearchContexts.Remove(tab);
    }

    private async Task AnalyzeSelectedSearchResultAsync(BrowserTab tab, string query, string sourceUrl,
        CancellationTokenSource cancellation)
    {
        var stopwatch = Stopwatch.StartNew();
        var candidateCount = 0;
        SledopytDiagnosticsService.Record("site-research", "started", "success");
        CrashReportService.AddBreadcrumb("sledopyt", "site-research-started");
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellation.Token);
            if (!sourceUrl.Equals(tab.CurrentUrl, StringComparison.OrdinalIgnoreCase) ||
                UrlService.IsInternal(tab.CurrentUrl) || UrlService.IsSearchProviderUrl(tab.CurrentUrl)) return;
            var sourceTitle = tab.Title;
            Dispatcher.Invoke(() =>
            {
                SshTerminalDock.Visibility = Visibility.Collapsed;
                LocalAiDock.BeginBackgroundResearch(tab, query);
                NetworkStatusText.Text = "Следопыт анализирует выбранный сайт…";
            });
            var pageText = await tab.GetReadablePageTextAsync();
            SledopytDiagnosticsService.Record("site-research", "page-read", "success",
                stopwatch.ElapsedMilliseconds);
            var links = await tab.GetResearchLinksAsync(query, 8);
            candidateCount = links.Count + 1;
            SledopytDiagnosticsService.Record("site-research", "links-read", "success",
                stopwatch.ElapsedMilliseconds, candidateCount);
            var progress = new Progress<string>(message => LocalAiDock.UpdateBackgroundResearchProgress(tab, message));
            var report = await NexusSearchService.AnalyzeSelectedSiteAsync(
                query, sourceTitle, sourceUrl, pageText, links, progress, cancellation.Token);
            if (!_isPrivate)
                await KnowledgeGraphService.RecordResearchAsync(report, cancellation.Token, "исследование выбранного сайта");
            SledopytDiagnosticsService.Record("site-research", "completed", "success",
                stopwatch.ElapsedMilliseconds, candidateCount, report.Items.Count);
            CrashReportService.AddBreadcrumb("sledopyt", "site-research-completed");
            Dispatcher.Invoke(() =>
            {
                LocalAiDock.StoreBackgroundResearch(tab, sourceUrl, report);
                NetworkStatusText.Text = $"Следопыт: выжимка готова · источников сайта {report.Items.Count}";
            });
        }
        catch (OperationCanceledException)
        {
            SledopytDiagnosticsService.Record("site-research", "cancelled", "partial",
                stopwatch.ElapsedMilliseconds, candidateCount, code: "navigation-or-timeout");
            if (!_siteResearchOperations.TryGetValue(tab, out var active) || !ReferenceEquals(active, cancellation))
                return;
            Dispatcher.Invoke(() =>
            {
                LocalAiDock.FailBackgroundResearch("Анализ выбранного сайта остановлен.");
                NetworkStatusText.Text = "Анализ выбранного сайта остановлен.";
            });
        }
        catch (Exception ex)
        {
            SledopytDiagnosticsService.Record("site-research", "failed", "failed",
                stopwatch.ElapsedMilliseconds, candidateCount, code: ClassifySledopytFailure(ex));
            CrashReportService.AddBreadcrumb("sledopyt", "site-research-failed");
            Dispatcher.Invoke(() =>
            {
                LocalAiDock.FailBackgroundResearch(ex.Message);
                NetworkStatusText.Text = "Следопыт: " + ex.Message;
            });
        }
        finally
        {
            if (_siteResearchOperations.TryGetValue(tab, out var active) && ReferenceEquals(active, cancellation))
                _siteResearchOperations.Remove(tab);
            cancellation.Dispose();
        }
    }

    private static string ClassifySledopytFailure(Exception ex) => ex switch
    {
        TimeoutException => "timeout",
        HttpRequestException => "network",
        JsonException => "invalid-response",
        _ => "operation-error"
    };

    private static bool IsSameSite(string left, string right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        (left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
         left.EndsWith('.' + right, StringComparison.OrdinalIgnoreCase) ||
         right.EndsWith('.' + left, StringComparison.OrdinalIgnoreCase));

    private async Task RunNexusSearchAsync(BrowserTab tab, string query)
    {
        query = query.Trim();
        if (query.Length < 2) return;
        if (_searchOperations.Remove(tab, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        // The first offline model start can take tens of seconds on a 16 GB PC.
        // Individual AI stages have their own shorter budgets and deterministic
        // fallbacks; this outer limit only protects the whole research session.
        var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        _searchOperations[tab] = cancellation;
        try
        {
            var internalUrl = "https://nexus.local/search.html?q=" + Uri.EscapeDataString(query);
            if (!await tab.NavigateInternalAndWaitAsync(internalUrl, TimeSpan.FromSeconds(12)))
                throw new InvalidOperationException("Не удалось открыть локальную страницу результатов Nexus.");
            var progress = new Progress<string>(message =>
            {
                if (tab.Core is null || !tab.CurrentUrl.StartsWith("https://nexus.local/search.html", StringComparison.OrdinalIgnoreCase)) return;
                _ = tab.Core.ExecuteScriptAsync("window.nexusStatus?.(" + JsonSerializer.Serialize(message) + ")");
            });
            var report = await NexusSearchService.SearchAsync(query, progress, cancellation.Token);
            if (!_isPrivate)
                await KnowledgeGraphService.RecordResearchAsync(report, cancellation.Token);
            if (tab.Core is null || !tab.CurrentUrl.StartsWith("https://nexus.local/search.html", StringComparison.OrdinalIgnoreCase)) return;
            var json = JsonSerializer.Serialize(report, WebJson);
            await tab.Core.ExecuteScriptAsync("window.nexusRender?.(" + json + ")");
            NetworkStatusText.Text = $"Nexus Search: проанализировано источников {report.Items.Count}";
        }
        catch (OperationCanceledException)
        {
            if (tab.Core is not null && tab.CurrentUrl.StartsWith("https://nexus.local/search.html", StringComparison.OrdinalIgnoreCase))
                await tab.Core.ExecuteScriptAsync("window.nexusStatus?.('Поиск остановлен или превысил лимит времени.')");
        }
        catch (Exception ex)
        {
            if (tab.Core is not null && tab.CurrentUrl.StartsWith("https://nexus.local/search.html", StringComparison.OrdinalIgnoreCase))
                await tab.Core.ExecuteScriptAsync("window.nexusStatus?.(" + JsonSerializer.Serialize(ex.Message) + ")");
        }
        finally
        {
            if (_searchOperations.TryGetValue(tab, out var current) && ReferenceEquals(current, cancellation))
                _searchOperations.Remove(tab);
            cancellation.Dispose();
        }
    }

    private void SyncUi()
    {
        var tab = ActiveTab;
        if (tab is null) return;

        if (!AddressBox.IsKeyboardFocusWithin)
            AddressBox.Text = UrlService.IsInternal(tab.CurrentUrl) ? string.Empty : tab.CurrentUrl;
        BackButton.IsEnabled = tab.CanGoBack;
        ForwardButton.IsEnabled = tab.CanGoForward;
        ReloadButton.Content = tab.IsLoading ? "\uE71A" : "\uE72C";
        ReloadButton.ToolTip = tab.IsLoading ? "Остановить" : "Обновить / остановить (F5)";
        LoadingBar.Visibility = tab.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        BookmarkButton.Content = BookmarkService.Contains(tab.CurrentUrl) ? "\uE735" : "\uE734";
        ConnectionGlyph.Text = tab.PhishingRisk != PhishingRiskLevel.None
            ? "\uE7BA"
            : tab.CurrentUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "\uE72E" : "\uE785";
        ConnectionGlyph.Foreground = tab.PhishingRisk switch
        {
            PhishingRiskLevel.High => Brushes.OrangeRed,
            PhishingRiskLevel.Medium => Brushes.DarkOrange,
            _ when tab.CurrentUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) =>
                (Brush)FindResource("AccentBrush"),
            _ => Brushes.DarkOrange
        };
        ConnectionGlyph.ToolTip = string.IsNullOrWhiteSpace(tab.SecurityWarning)
            ? "Сведения о соединении"
            : tab.SecurityWarning;
        TopologySiteText.Text = string.IsNullOrWhiteSpace(tab.CurrentHost) ? tab.Title : tab.CurrentHost;
        TopologyProxyText.Text = SettingsService.Current.EnableCustomProxy
            ? $"{SettingsService.Current.ProxyKind.ToString().ToUpperInvariant()} · {SettingsService.Current.ProxyHost}:{SettingsService.Current.ProxyPort}"
            : "Прямое подключение";
        RefreshLocalPorts();
        TopologyLocalPortsText.Text = _localPortsAvailable
            ? $"TCP {_localTcpPorts.Length} · UDP {_localUdpPorts.Length}"
            : "Нет доступа";
        var snapshot = tab.GetNetworkSnapshot();
        var route = SettingsService.Current.EnableCustomProxy ? "прокси браузера" : "системный маршрут";
        DownloadRateText.Text = $"↓ {FormatRate(_downloadBytesPerSecond)}";
        DownloadRateText.Foreground = RateBrush(_downloadBytesPerSecond);
        UploadRateText.Text = $"↑ {FormatRate(_uploadBytesPerSecond)}";
        UploadRateText.Foreground = RateBrush(_uploadBytesPerSecond);
        PingText.Text = $"PING LAN {(_pingMilliseconds is null ? "—" : _pingMilliseconds + " мс")}";
        PingText.Foreground = PingBrush(_pingMilliseconds);
        NetworkDetailsText.Text = $"  ·  {(tab.IsSecureConnection ? "TLS защищён" : "без TLS")}  ·  {route}  ·  " +
                                  $"сайт: {(string.IsNullOrWhiteSpace(tab.CurrentHost) ? "—" : tab.CurrentHost)}  ·  " +
                                  $"запросы: {snapshot.RequestCount}  ·  порты: {FormatInline(snapshot.ObservedPorts)}  ·  " +
                                  $"сторонние узлы: {snapshot.ThirdPartyHosts.Count}  ·  заблокировано: {tab.BlockedCount}";
        Title = (_isPrivate ? "Nexus Monach — приватно" : "Nexus Monach") +
                (string.IsNullOrWhiteSpace(tab.Title) ? string.Empty : " · " + tab.Title);
    }

    private void UpdatePrivacyLabel()
    {
        PrivacyLevelText.Text = SettingsService.Current.PrivacyLevel switch
        {
            PrivacyLevel.Basic => "Защита: базовая",
            PrivacyLevel.Strict => "Защита: строгая",
            _ => "Защита: сбалансированная"
        };
    }

    private void RefreshLocalPorts()
    {
        if (DateTime.UtcNow - _localPortsUpdatedUtc < TimeSpan.FromSeconds(5)) return;
        try
        {
            _localPortDetails = WindowsPortService.GetListeningPorts().ToArray();
            _localTcpPorts = _localPortDetails.Where(x => x.Protocol == "TCP").Select(x => x.Port).Distinct().OrderBy(x => x).ToArray();
            _localUdpPorts = _localPortDetails.Where(x => x.Protocol == "UDP").Select(x => x.Port).Distinct().OrderBy(x => x).ToArray();
            _localPortsAvailable = _localPortDetails.Length > 0;
        }
        catch
        {
            _localTcpPorts = [];
            _localUdpPorts = [];
            _localPortDetails = [];
            _localPortsAvailable = false;
        }
        _localPortsUpdatedUtc = DateTime.UtcNow;
    }

    private void ShowTopologyDetails(string heading, string summary, string details)
    {
        new TopologyDetailsWindow(heading, summary, details) { Owner = this }.ShowDialog();
    }

    private void BrowserNode_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Current;
        var runtime = Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString();
        ShowTopologyDetails("Nexus Monach",
            "Локальная оболочка браузера и активные механизмы защиты.",
            $"Режим окна: {(_isPrivate ? "InPrivate" : "обычный")}\n" +
            $"Уровень защиты: {settings.PrivacyLevel}\n" +
            $"Do Not Track: {(settings.SendDoNotTrack ? "включён" : "выключен")}\n" +
            $"Global Privacy Control: {(settings.SendGlobalPrivacyControl ? "включён" : "выключен")}\n" +
            $"Защита WebRTC: {(settings.PreventWebRtcIpLeak ? "включена" : "выключена")}\n" +
            $"Экономия памяти: {(settings.MemorySaver ? "включена" : "выключена")}\n" +
            $"WebView2 Runtime: {runtime}");
    }

    private void SystemRouteNode_Click(object sender, RoutedEventArgs e)
    {
        var interfaces = NetworkInterface.GetAllNetworkInterfaces()
            .Where(x => x.OperationalStatus == OperationalStatus.Up)
            .Select(x =>
            {
                var ip = x.GetIPProperties();
                var addresses = string.Join(", ", ip.UnicastAddresses.Select(a => a.Address.ToString()));
                var gateways = string.Join(", ", ip.GatewayAddresses.Select(a => a.Address.ToString()));
                var dns = string.Join(", ", ip.DnsAddresses.Select(a => a.ToString()));
                return $"{x.Name}\n  тип: {x.NetworkInterfaceType}; скорость: {x.Speed / 1_000_000d:0.#} Мбит/с\n" +
                       $"  IP: {addresses}\n  шлюз: {gateways}\n  DNS: {dns}";
            }).ToArray();
        ShowTopologyDetails("Системный маршрут",
            $"Активных сетевых интерфейсов: {interfaces.Length}. VPN может отображаться как отдельный адаптер.",
            interfaces.Length == 0 ? "Активные интерфейсы не найдены." : string.Join("\n\n", interfaces));
    }

    private void LocalPortsNode_Click(object sender, RoutedEventArgs e)
    {
        _localPortsUpdatedUtc = DateTime.MinValue;
        RefreshLocalPorts();
        static string Ports(IEnumerable<int> values) => string.Join(", ", values.DefaultIfEmpty().Select(x => x == 0 ? "—" : x.ToString()));
        static string Details(IEnumerable<LocalPortInfo> values) => string.Join("\n", values.Select(x =>
            $"{x.Protocol,-3} {x.Address}:{x.Port,-5}  {x.ProcessName} (PID {x.ProcessId})").DefaultIfEmpty("—"));
        ShowTopologyDetails("Мой компьютер: слушающие порты",
            _localPortsAvailable
                ? $"Локально обнаружено TCP: {_localTcpPorts.Length}, UDP: {_localUdpPorts.Length}. Это не означает, что каждый порт доступен из интернета."
                : "Windows не предоставила список локальных слушающих портов.",
            $"TCP до 1000:\n{Ports(_localTcpPorts.Where(x => x <= 1000))}\n\n" +
            $"UDP до 1000:\n{Ports(_localUdpPorts.Where(x => x <= 1000))}\n\n" +
            $"Все TCP:\n{Ports(_localTcpPorts)}\n\nВсе UDP:\n{Ports(_localUdpPorts)}\n\n" +
            $"Процессы и точки прослушивания:\n{Details(_localPortDetails)}");
    }

    private void RoutingNode_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Current;
        var summary = settings.EnableCustomProxy
            ? $"Браузер направляет трафик через {settings.ProxyKind}."
            : "Встроенный прокси выключен; действует системный маршрут Windows/VPN.";
        ShowTopologyDetails("Маршрутизация", summary,
            $"Встроенный прокси: {(settings.EnableCustomProxy ? "включён" : "выключен")}\n" +
            $"Тип: {settings.ProxyKind}\nУзел: {settings.ProxyHost}:{settings.ProxyPort}\n" +
            $"Прямые исключения:\n{(string.IsNullOrWhiteSpace(settings.ProxyBypassList) ? "—" : settings.ProxyBypassList)}\n\n" +
            "Изменение прокси требует перезапуска браузера.");
    }

    private void InternetNode_Click(object sender, RoutedEventArgs e)
    {
        var tab = ActiveTab;
        if (tab is null) return;
        var snapshot = tab.GetNetworkSnapshot();
        ShowTopologyDetails("Интернет-соединения",
            $"За текущую загрузку наблюдалось {snapshot.RequestCount} HTTP(S)-запросов к {snapshot.ContactedHosts.Count} узлам.",
            $"Текущий URL:\n{tab.CurrentUrl}\n\n" +
            $"Шифрование основного адреса: {(tab.IsSecureConnection ? "HTTPS / TLS" : "нет HTTPS")}\n" +
            $"Наблюдаемые порты: {FormatList(snapshot.ObservedPorts)}\n\n" +
            $"Все узлы:\n{FormatList(snapshot.ContactedHosts)}");
    }

    private void CurrentSiteNode_Click(object sender, RoutedEventArgs e)
    {
        var tab = ActiveTab;
        if (tab is null) return;
        var snapshot = tab.GetNetworkSnapshot();
        var lowPorts = snapshot.ObservedPorts.Where(x => x <= 1000).ToArray();
        ShowTopologyDetails("Текущий сайт: " + (string.IsNullOrWhiteSpace(tab.CurrentHost) ? tab.Title : tab.CurrentHost),
            $"Сайт обращался к {snapshot.ThirdPartyHosts.Count} сторонним узлам; заблокировано трекеров: {snapshot.BlockedTrackerHosts.Count}.",
            $"Адрес:\n{tab.CurrentUrl}\n\n" +
            $"Реально использованные порты до 1000:\n{FormatList(lowPorts)}\n\n" +
            $"Сторонние получатели запросов:\n{FormatList(snapshot.ThirdPartyHosts)}\n\n" +
            $"Заблокированные трекеры:\n{FormatList(snapshot.BlockedTrackerHosts)}\n\n" +
            "Важно: сторонний узел может быть CDN, шрифтом, API или трекером; сам факт соединения не доказывает слежку.\n" +
            "Активное сканирование портов чужого сервера не выполняется.");
    }

    private static string FormatList<T>(IEnumerable<T> values)
    {
        var items = values.Select(x => x is null ? null : x.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return items.Length == 0 ? "—" : string.Join("\n", items);
    }

    private static string FormatInline<T>(IEnumerable<T> values)
    {
        var items = values.Take(8).Select(x => x is null ? null : x.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return items.Length == 0 ? "—" : string.Join(",", items);
    }

    private async void TabsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ActiveTab is not null)
            await EnsureTabReadyAsync(ActiveTab);
    }

    private async void NewTab_Click(object sender, RoutedEventArgs e) => await OpenTabAsync();

    private void TabsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left ||
            ItemsControl.ContainerFromElement(TabsList, e.OriginalSource as DependencyObject) is not null)
            return;

        e.Handled = true;
        _ = OpenTabAsync();
    }

    private void CloseTab_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if ((sender as FrameworkElement)?.Tag is BrowserTab tab)
            CloseTab(tab);
    }

    private void CloseTab(BrowserTab tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;
        if (ReferenceEquals(BrowserHost.Content, tab.View))
            BrowserHost.Content = null;
        _lastKnowledgeUrl.Remove(tab);
        _pendingSearchFollowUp.Remove(tab);
        StopSelectedSiteResearch(tab, forgetContext: true);
        if (_searchOperations.Remove(tab, out var search)) { search.Cancel(); search.Dispose(); }
        Tabs.Remove(tab);
        tab.Dispose();

        if (Tabs.Count == 0)
            Close();
        else
            TabsList.SelectedIndex = Math.Min(index, Tabs.Count - 1);
    }

    private async void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await NavigateOrSearchAsync(AddressBox.Text);
    }

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) => AddressBox.SelectAll();
    private async void SearchButton_Click(object sender, RoutedEventArgs e) => await NavigateOrSearchAsync(AddressBox.Text);
    private void Back_Click(object sender, RoutedEventArgs e) => ActiveTab?.GoBack();
    private void Forward_Click(object sender, RoutedEventArgs e) => ActiveTab?.GoForward();
    private void Reload_Click(object sender, RoutedEventArgs e) => ActiveTab?.ReloadOrStop();
    private void Home_Click(object sender, RoutedEventArgs e) => ActiveTab?.Navigate(UrlService.GetHomePage());

    private async void Bookmark_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab is null) return;
        await BookmarkService.ToggleAsync(ActiveTab.Title, ActiveTab.CurrentUrl);
        SyncUi();
    }

    private void MenuButton_Click(object sender, RoutedEventArgs e)
    {
        if (MenuButton.ContextMenu is null) return;
        MenuButton.ContextMenu.PlacementTarget = MenuButton;
        MenuButton.ContextMenu.IsOpen = true;
    }

    private async void NewPrivateWindow_Click(object sender, RoutedEventArgs e)
    {
        var window = new MainWindow(isPrivate: true) { Opacity = 0 };
        window.Show();
        await window.InitializeAsync(waitForFirstPage: true);
        window.Opacity = 1;
        window.Activate();
    }

    private void ShowBookmarks_Click(object sender, RoutedEventArgs e)
    {
        var window = DataWindow.ForBookmarks(async url => await OpenTabAsync(url));
        window.Owner = this;
        window.ShowDialog();
    }

    private void ShowDownloads_Click(object sender, RoutedEventArgs e)
    {
        var window = DataWindow.ForDownloads();
        window.Owner = this;
        window.Show();
    }

    private async Task IndexKnowledgeAsync(BrowserTab tab, string expectedUrl, string? previousUrl)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(5));
            if (!tab.CurrentUrl.Equals(expectedUrl, StringComparison.OrdinalIgnoreCase)) return;
            var text = await tab.GetReadablePageTextAsync();
            await KnowledgeGraphService.IndexPageAsync(tab.Title, expectedUrl, text, previousUrl);
        }
        catch { /* Индексация не должна мешать просмотру страницы. */ }
    }

    private async void ShowSmartCapsules_Click(object sender, RoutedEventArgs e)
    {
        if (Tabs.Count == 0) return;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var source = Tabs.Select(x => (x.Title, x.CurrentUrl)).ToArray();
            var capsules = await LocalIntelligenceService.BuildCapsulesAsync(source);
            Mouse.OverrideCursor = null;
            new SmartCapsulesWindow(capsules, ArchiveCapsuleAsync) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            GlassDialogWindow.Show(this, ex.Message, "Smart Capsules", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally { Mouse.OverrideCursor = null; }
    }

    private async Task ArchiveCapsuleAsync(SmartCapsule capsule)
    {
        await KnowledgeGraphService.AddCapsuleAsync(capsule);
        var urls = capsule.Urls.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var tab in Tabs.Where(x => urls.Contains(x.CurrentUrl)).ToList())
        {
            if (Tabs.Count > 1) CloseTab(tab);
            else tab.Navigate(UrlService.NewTabUrl);
        }
    }

    private void ShowKnowledgeGraph_Click(object sender, RoutedEventArgs e)
    {
        new KnowledgeGraphWindow(KnowledgeGraphService.Snapshot(), async url => await OpenTabAsync(url))
        { Owner = this }.ShowDialog();
    }

    private void ShowExtensions_Click(object sender, RoutedEventArgs e)
    {
        if (_isPrivate || ActiveTab?.Core is null) return;
        new ExtensionsWindow(ActiveTab.Core.Profile) { Owner = this }.ShowDialog();
    }

    private async void TranslateTop_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab?.Core is null) return;
        await LocalAiDock.TranslateCurrentPageAsync(ActiveTab);
    }

    private void TranslateMenu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { ContextMenu: { } menu } button) return;
        menu.PlacementTarget = button;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private async void TranslateVideoAudio_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab?.Core is null) return;
        await LocalAiDock.TranslateVideoAudioAsync(ActiveTab);
    }

    private async void ShoppingAgentTop_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab?.Core is null) return;
        SshTerminalDock.Visibility = Visibility.Collapsed;
        await LocalAiDock.PrepareShoppingAgentAsync(ActiveTab);
    }

    private void SshTerminal_Click(object sender, RoutedEventArgs e)
    {
        LocalAiDock.Visibility = Visibility.Collapsed;
        SshTerminalDock.Visibility = SshTerminalDock.Visibility == Visibility.Visible
            ? Visibility.Collapsed : Visibility.Visible;
        if (SshTerminalDock.Visibility == Visibility.Visible)
            SshTerminalDock.FocusTarget();
    }

    private void SshTerminal_CloseRequested(object? sender, EventArgs e) =>
        SshTerminalDock.Visibility = Visibility.Collapsed;

    private void ShowDeveloperTools_Click(object sender, RoutedEventArgs e)
    {
        if (ActiveTab?.Core is null) return;
        SshTerminalDock.Visibility = Visibility.Collapsed;
        ActiveTab.Core.OpenDevToolsWindow();
    }

    private async void ShowPrivacyMonitor_Click(object sender, RoutedEventArgs e)
    {
        var show = PrivacyDock.Visibility != Visibility.Visible;
        await PrivacyDock.SetEnabledAsync(show);
    }

    private void ShowSettings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowGuardianCenter_Click(object sender, RoutedEventArgs e)
    {
        var window = new GuardianCenterWindow { Owner = this };
        window.ShowDialog();
    }

    private async void ShowSettings()
    {
        var window = new SettingsWindow(SettingsService.Current.Clone()) { Owner = this };
        if (window.ShowDialog() != true || window.ResultSettings is null) return;
        var extensionsChanged = SettingsService.Current.EnableExtensions != window.ResultSettings.EnableExtensions;
        var proxyChanged = SettingsService.Current.EnableCustomProxy != window.ResultSettings.EnableCustomProxy ||
                           SettingsService.Current.PreventWebRtcIpLeak != window.ResultSettings.PreventWebRtcIpLeak ||
                           SettingsService.Current.ProxyKind != window.ResultSettings.ProxyKind ||
                           SettingsService.Current.ProxyPort != window.ResultSettings.ProxyPort ||
                           !SettingsService.Current.ProxyHost.Equals(window.ResultSettings.ProxyHost, StringComparison.OrdinalIgnoreCase) ||
                           !SettingsService.Current.ProxyBypassList.Equals(window.ResultSettings.ProxyBypassList, StringComparison.OrdinalIgnoreCase);
        await SettingsService.SaveAsync(window.ResultSettings);
        foreach (var tab in Tabs.Where(x => x.IsInitialized))
            await tab.ApplySettingsAsync();
        UpdatePrivacyLabel();
        SyncUi();
        if (!_isPrivate)
            await PrivacyDock.SetEnabledAsync(SettingsService.Current.ShowPrivacyMonitor);
        ExtensionsMenuItem.IsEnabled = !_isPrivate && BrowserEnvironment.ExtensionsEnabledAtStartup;
        if (extensionsChanged || proxyChanged)
        {
            var reason = extensionsChanged && proxyChanged
                ? "Изменены поддержка расширений и сетевой прокси."
                : extensionsChanged ? "Изменена поддержка расширений." : "Изменены настройки сетевого прокси.";
            GlassDialogWindow.Show(this,
                reason + " Изменения вступят в силу после полного перезапуска Nexus Monach.",
                "Требуется перезапуск", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private async void ClearData_Click(object sender, RoutedEventArgs e)
    {
        if (GlassDialogWindow.Show(this,
                "Удалить cookies, кэш, разрешения сайтов, сохранённые формы и локальный граф знаний?\n\nПосле очистки сайты могут выйти из учётных записей.",
                "Очистка данных", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        if (ActiveTab?.Core is not null)
            await ActiveTab.Core.Profile.ClearBrowsingDataAsync();
        if (!_isPrivate)
        {
            await KnowledgeGraphService.ClearAsync();
            try { if (File.Exists(AppPaths.HistoryFile)) File.Delete(AppPaths.HistoryFile); } catch { }
        }
        GlassDialogWindow.Show(this, "Данные просмотра очищены.", "Nexus Monach",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void About_Click(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.T) { _ = OpenTabAsync(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.W && ActiveTab is not null) { CloseTab(ActiveTab); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.L) { AddressBox.Focus(); AddressBox.SelectAll(); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.N) { NewPrivateWindow_Click(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.K) { ShowSmartCapsules_Click(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.G) { ShowKnowledgeGraph_Click(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.D) { Bookmark_Click(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.J) { ShowDownloads_Click(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.Delete) { ClearData_Click(sender, e); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Left) { ActiveTab?.GoBack(); e.Handled = true; }
        else if (Keyboard.Modifiers == ModifierKeys.Alt && e.Key == Key.Right) { ActiveTab?.GoForward(); e.Handled = true; }
        else if (e.Key == Key.F12 ||
                 (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.I))
        { ShowDeveloperTools_Click(sender, e); e.Handled = true; }
        else if (e.Key == Key.F5) { ActiveTab?.ReloadOrStop(); e.Handled = true; }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsInteractiveElement(e.OriginalSource as DependencyObject)) return;
        if (e.ClickCount == 2) ToggleMaximize();
        else if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private static bool IsInteractiveElement(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is Button or ListBoxItem) return true;
            source = VisualTreeHelper.GetParent(source);
        }
        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void ToggleMaximize() => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void RestartWindow_Click(object sender, RoutedEventArgs e)
    {
        RequestSecureRestart();
    }

    public void RequestSecureRestart()
    {
        if (_closing) return;
        _restartRequested = true;
        Close();
    }

    private void WebView2RuntimeMonitor_StatusChanged(object? sender, WebView2RuntimeSnapshot snapshot) =>
        Dispatcher.BeginInvoke(new Action(() => HandleCoreUpdateSnapshot(snapshot)));

    private void HandleCoreUpdateSnapshot(WebView2RuntimeSnapshot snapshot)
    {
        if (_isPrivate || _closing || snapshot.State != WebView2RuntimeState.RestartRequired ||
            _coreUpdatePromptShown || !IsLoaded) return;
        _coreUpdatePromptShown = true;
        var answer = GlassDialogWindow.Show(this,
            "Microsoft Edge Update уже загрузила и установила новое ядро WebView2. " +
            "Оно начнёт работать после перезапуска Nexus Monach.\n\n" +
            $"Активная версия: {snapshot.ActiveVersion}\n" +
            $"Готовая версия: {snapshot.InstalledVersion}\n\n" +
            "Перезапустить сейчас? Обычные вкладки и разрешённые непарольные поля будут " +
            "локально зашифрованы средствами Windows и восстановлены после запуска. " +
            "Пароли, OTP, платёжные поля и приватные окна не сохраняются.",
            "Nexus Guardian · обновление ядра готово", MessageBoxButton.YesNo, MessageBoxImage.Information);
        if (answer == MessageBoxResult.Yes) RequestSecureRestart();
    }

    private void Window_StateChanged(object sender, EventArgs e)
    {
        MaximizeButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = WindowState == WindowState.Maximized ? "Восстановить" : "Развернуть";
        if (WindowState == WindowState.Maximized)
        {
            MaxWidth = SystemParameters.WorkArea.Width;
            MaxHeight = SystemParameters.WorkArea.Height;
        }
        else
        {
            MaxWidth = double.PositiveInfinity;
            MaxHeight = double.PositiveInfinity;
        }
    }

    private async void NetworkPerformanceTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var seconds = Math.Max(0.2, (now - _networkSampleUtc).TotalSeconds);
        _networkSampleUtc = now;
        var samples = new List<(double Down, double Up)>();
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces().Where(x =>
                         x.OperationalStatus == OperationalStatus.Up &&
                         x.NetworkInterfaceType != NetworkInterfaceType.Loopback))
            {
                var stats = adapter.GetIPStatistics();
                if (_networkCounters.TryGetValue(adapter.Id, out var previous))
                    samples.Add((Math.Max(0, stats.BytesReceived - previous.Received) / seconds,
                                 Math.Max(0, stats.BytesSent - previous.Sent) / seconds));
                _networkCounters[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
            }
            if (samples.Count > 0)
            {
                var active = samples.OrderByDescending(x => x.Down + x.Up).First();
                _downloadBytesPerSecond = active.Down;
                _uploadBytesPerSecond = active.Up;
            }
        }
        catch { /* Счётчики отдельных драйверов VPN могут быть недоступны. */ }

        if (++_networkTick % 5 == 0 && !_pingBusy)
        {
            _pingBusy = true;
            try
            {
                var gateway = GetDefaultGateway();
                if (gateway is null) _pingMilliseconds = null;
                else
                {
                    using var ping = new Ping();
                    var reply = await ping.SendPingAsync(gateway, 1500);
                    _pingMilliseconds = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
                }
            }
            catch { _pingMilliseconds = null; }
            finally { _pingBusy = false; }
        }
        SyncUi();
    }

    private static string? GetDefaultGateway()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(x => x.OperationalStatus == OperationalStatus.Up && x.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(x => x.GetIPProperties().GatewayAddresses)
                .Select(x => x.Address)
                .FirstOrDefault(x => !x.Equals(System.Net.IPAddress.Any) && !x.Equals(System.Net.IPAddress.IPv6Any))
                ?.ToString();
        }
        catch { return null; }
    }

    private static string FormatRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024) return $"{bytesPerSecond / (1024 * 1024):0.0} МБ/с";
        if (bytesPerSecond >= 1024) return $"{bytesPerSecond / 1024:0} КБ/с";
        return $"{bytesPerSecond:0} Б/с";
    }

    private static Brush RateBrush(double bytesPerSecond) => bytesPerSecond switch
    {
        <= 1 => new SolidColorBrush(Color.FromRgb(145, 162, 180)),
        >= 5 * 1024 * 1024 => new SolidColorBrush(Color.FromRgb(56, 217, 150)),
        >= 1024 * 1024 => new SolidColorBrush(Color.FromRgb(155, 225, 93)),
        >= 256 * 1024 => new SolidColorBrush(Color.FromRgb(218, 185, 106)),
        >= 64 * 1024 => new SolidColorBrush(Color.FromRgb(228, 123, 53)),
        _ => new SolidColorBrush(Color.FromRgb(140, 47, 75))
    };

    private static Brush PingBrush(long? ping) => ping switch
    {
        null => new SolidColorBrush(Color.FromRgb(140, 47, 75)),
        <= 40 => new SolidColorBrush(Color.FromRgb(56, 217, 150)),
        <= 80 => new SolidColorBrush(Color.FromRgb(155, 225, 93)),
        <= 150 => new SolidColorBrush(Color.FromRgb(218, 185, 106)),
        <= 250 => new SolidColorBrush(Color.FromRgb(228, 123, 53)),
        _ => new SolidColorBrush(Color.FromRgb(140, 47, 75))
    };

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeReady) return;
        e.Cancel = true;
        if (_closing) return;
        _closing = true;

        // Always unwind the original WPF Closing event before the asynchronous
        // shutdown pipeline eventually calls Close() again. Some fast paths do
        // not otherwise yield, and WPF rejects a nested Close while the first
        // Closing notification is still being dispatched.
        await Dispatcher.Yield(DispatcherPriority.Background);

        _memoryTimer.Stop();
        _networkPerformanceTimer.Stop();
        LocalAiDock.StopVideoTranslation();
        SshTerminalDock.Disconnect();
        foreach (var search in _searchOperations.Values) search.Cancel();

        if (!_isPrivate && _restartRequested)
        {
            try
            {
                var states = await Task.WhenAll(Tabs.Take(20).Select(async tab =>
                {
                    try
                    {
                        return await tab.CaptureSecureRestartStateAsync().WaitAsync(TimeSpan.FromSeconds(2));
                    }
                    catch
                    {
                        return SecureRestartSessionService.UrlOnly(tab.CurrentUrl);
                    }
                }));
                await SecureRestartSessionService.SaveAsync(states, Math.Max(0, TabsList.SelectedIndex));

                // The restart snapshot is DPAPI-encrypted. Do not leave a second,
                // plaintext copy of the same tab list in the ordinary session file.
                try { if (File.Exists(AppPaths.SessionFile)) File.Delete(AppPaths.SessionFile); }
                catch { /* The encrypted snapshot remains the authoritative restart state. */ }
            }
            catch (Exception ex)
            {
                CrashReportService.RecordNonFatal("guardian", "secure-restart-session-write", ex);
                _restartRequested = false;
                _closing = false;
                _memoryTimer.Start();
                _networkPerformanceTimer.Start();
                GlassDialogWindow.Show(this,
                    "Безопасный перезапуск отменён: Nexus не смог зашифровать состояние вкладок. " +
                    "Окно оставлено открытым, чтобы данные не потерялись.\n\n" + ex.Message,
                    "Nexus Guardian", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        else if (!_isPrivate && SettingsService.Current.ClearBrowsingDataOnExit && ActiveTab?.Core is not null)
        {
            try { await ActiveTab.Core.Profile.ClearBrowsingDataAsync(); }
            catch { /* Закрытие продолжится даже при повреждённом профиле. */ }
            await KnowledgeGraphService.ClearAsync();
            try { if (File.Exists(AppPaths.HistoryFile)) File.Delete(AppPaths.HistoryFile); } catch { }
            if (File.Exists(AppPaths.SessionFile))
                File.Delete(AppPaths.SessionFile);
            SecureRestartSessionService.Delete();
        }
        else if (!_isPrivate && SettingsService.Current.RestoreSession)
        {
            var urls = Tabs.Select(x => x.CurrentUrl).ToList();
            await SessionService.SaveAsync(urls, Math.Max(0, TabsList.SelectedIndex));
        }

        BrowserHost.Content = null;
        foreach (var tab in Tabs.ToList()) tab.Dispose();
        Tabs.Clear();
        var restartRequested = _restartRequested;
        _closeReady = true;
        Close();
        if (restartRequested) StartNewInstance();
    }

    private static void StartNewInstance()
    {
        try
        {
            var guardian = Path.Combine(AppContext.BaseDirectory, "NexusMonach.exe");
            var executable = File.Exists(guardian) ? guardian : Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable)) return;
            var info = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                WorkingDirectory = AppContext.BaseDirectory
            };
            var commandLine = Environment.GetCommandLineArgs();
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase) && commandLine.Length > 0)
                info.ArgumentList.Add(commandLine[0]);
            foreach (var argument in commandLine.Skip(1)) info.ArgumentList.Add(argument);
            Process.Start(info);
        }
        catch { /* Если Windows запретила перезапуск, текущее окно всё равно корректно закроется. */ }
    }

    private async void MemoryTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        foreach (var tab in Tabs)
            tab.UpdateVisualDecay(ReferenceEquals(tab, ActiveTab), now);

        if (!SettingsService.Current.MemorySaver) return;
        var cutoff = DateTime.UtcNow - TimeSpan.FromMinutes(10);
        foreach (var tab in Tabs.Where(x => !ReferenceEquals(x, ActiveTab) && x.IsInitialized && x.LastActivatedUtc < cutoff))
            await tab.TrySuspendAsync();
    }
}
