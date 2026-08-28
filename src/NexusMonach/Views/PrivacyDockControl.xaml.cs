using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NexusMonach.Models;
using NexusMonach.Services;
using SettingsService = NexusMonach.Services.SettingsService;

namespace NexusMonach.Views;

/// <summary>
/// Панель «Приватность и защита»: рабочее место управления сетевой цепочкой
/// (сервер / маршрут / прокси) и защитой (След, Дозор, порт-щит, мост,
/// страж WebRTC) — всё, что раньше было разбросано по настройкам.
/// Скрыта по умолчанию; возвращается галочкой в настройках.
/// </summary>
public partial class PrivacyDockControl : UserControl
{
    private sealed record Choice<T>(string Label, T Value);

    private DispatcherTimer? _refresh;
    private bool _suppressEvents;

    public PrivacyDockControl()
    {
        InitializeComponent();
    }

    /// <summary>Включает/выключает панель (совместимо с вызовами MainWindow).</summary>
    public Task SetEnabledAsync(bool enabled)
    {
        if (enabled)
        {
            LoadState();
            StartRefresh();
        }
        else
            _refresh?.Stop();
        return Task.CompletedTask;
    }

    /// <summary>Совместимость со старым монитором: транспорт сайта больше не показываем.</summary>
    public void SetCurrentTransport(string _) { }

    private void Control_Loaded(object sender, RoutedEventArgs e)
    {
        PortShieldCombo.ItemsSource = new Choice<Services.PortShieldMode>[]
        {
            new("Порт-щит: авто (один UAC)", Services.PortShieldMode.Auto),
            new("Порт-щит: только уведомлять", Services.PortShieldMode.NotifyOnly),
            new("Порт-щит: выключен", Services.PortShieldMode.Off)
        };
        LoadState();
        StartRefresh();
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e) => _refresh?.Stop();

    private void StartRefresh()
    {
        if (_refresh is not null) return;
        _refresh = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        _refresh.Tick += (_, _) => RefreshChain();
        _refresh.Start();
    }

    private void LoadState()
    {
        _suppressEvents = true;
        var settings = SettingsService.Current;
        VlessUriBox.Text = settings.VlessProfileUri;
        ProxyHostBox.Text = settings.ProxyHost;
        ProxyPortBox.Text = settings.ProxyPort.ToString();
        ProxyKindCombo.ItemsSource = new Choice<ProxyKind>[]
        {
            new("SOCKS5", ProxyKind.Socks5),
            new("HTTP", ProxyKind.Http)
        };
        ProxyKindCombo.SelectedIndex = settings.ProxyKind == ProxyKind.Http ? 1 : 0;
        TrailModeCheck.IsChecked = settings.TrailModeEnabled;
        WatchdogCheck.IsChecked = settings.NetworkWatchdogEnabled;
        RelayCheck.IsChecked = settings.TorRelayEnabled;
        WebRtcLeakCheck.IsChecked = settings.PreventWebRtcIpLeak;
        PortShieldCombo.SelectedIndex = settings.PortShieldMode switch
        {
            Services.PortShieldMode.Auto => 0,
            Services.PortShieldMode.Off => 2,
            _ => 1
        };
        _suppressEvents = false;
        RefreshChain();
    }

    /// <summary>Обновляет тумблеры и статус по фактическому снимку цепочки.</summary>
    private void RefreshChain()
    {
        var snapshot = Services.NetworkChainService.Snapshot();
        ChainStatusText.Text = snapshot.StatusText +
            (snapshot.VlessRunning ? $" (SOCKS {Services.Vless.VlessRuntime.SocksPort})" : "");
        VlessToggle.Content = snapshot.VlessRunning
            ? "Сервер: подключён" : "Сервер: выкл";
        TorToggle.Content = snapshot.TorInChain
            ? (snapshot.TorWrapped ? "Маршрут: в цепочке" : "Маршрут: ждёт туннель")
            : "Маршрут: вне цепочки";
        ProxyToggle.Content = snapshot.ProxyEnabled ? "Прокси: вкл" : "Прокси: выкл";
    }

    // ── Тумблеры цепочки ──────────────────────────────────────────

    private async void VlessToggle_Click(object sender, RoutedEventArgs e)
    {
        VlessToggle.IsEnabled = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(VlessUriBox.Text))
            {
                var settings = SettingsService.Current;
                settings.VlessProfileUri = VlessUriBox.Text.Trim();
                await SettingsService.SaveAsync(settings);
            }
            await Services.NetworkChainService.ToggleVlessAsync();
        }
        finally { VlessToggle.IsEnabled = true; }
        RefreshChain();
    }

    private async void TorToggle_Click(object sender, RoutedEventArgs e)
    {
        TorToggle.IsEnabled = false;
        try { await Services.NetworkChainService.ToggleTorAsync(); }
        finally { TorToggle.IsEnabled = true; }
        RefreshChain();
    }

    private async void ProxyToggle_Click(object sender, RoutedEventArgs e)
    {
        ProxyToggle.IsEnabled = false;
        try
        {
            // Конфигурация живёт здесь же: сохраняем поля до переключения.
            var settings = SettingsService.Current;
            settings.ProxyHost = ProxyHostBox.Text.Trim();
            if (int.TryParse(ProxyPortBox.Text.Trim(), out var port))
                settings.ProxyPort = port;
            settings.ProxyKind = ProxyKindCombo.SelectedItem is Choice<ProxyKind> kind
                ? kind.Value : ProxyKind.Socks5;
            await SettingsService.SaveAsync(settings);
            await Services.NetworkChainService.ToggleProxyAsync();
        }
        finally { ProxyToggle.IsEnabled = true; }
        RefreshChain();
    }

    private async void VlessConnect_Click(object sender, RoutedEventArgs e)
    {
        VlessConnectButton.IsEnabled = false;
        VlessConnectButton.Content = "Подключаю…";
        try
        {
            var settings = SettingsService.Current;
            settings.VlessProfileUri = VlessUriBox.Text.Trim();
            await SettingsService.SaveAsync(settings);
            await Services.NetworkChainService.EnsureVlessAsync();
        }
        finally
        {
            VlessConnectButton.IsEnabled = true;
            VlessConnectButton.Content = "Подключить";
        }
        RefreshChain();
    }

    // ── Защита ────────────────────────────────────────────────────

    private async void TrailMode_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var settings = SettingsService.Current;
        settings.TrailModeEnabled = TrailModeCheck.IsChecked == true;
        if (settings.TrailModeEnabled)
            Services.Tor.TrailMode.Apply(settings);
        await SettingsService.SaveAsync(settings);
        Services.CrashReportService.AddBreadcrumb("dock", "trail-" + settings.TrailModeEnabled);
    }

    private async void Watchdog_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var settings = SettingsService.Current;
        settings.NetworkWatchdogEnabled = WatchdogCheck.IsChecked == true;
        await SettingsService.SaveAsync(settings);
        Services.VoiceAssistantService.Announce(
            settings.NetworkWatchdogEnabled
                ? "Сетевой Дозор включён. Защита начнётся после перезапуска браузера."
                : "Сетевой Дозор выключен. До конца сессии ловушки продолжают работать.",
            Services.VoiceAnnouncementPriority.Important);
    }

    private async void Relay_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var settings = SettingsService.Current;
        var enable = RelayCheck.IsChecked == true;

        // Включение моста — осознанное решение: человек должен понимать,
        // что увидит провайдер и когда мост реально полезен.
        if (enable && !settings.TorRelayAcknowledged)
        {
            var answer = GlassDialogWindow.Show(Window.GetWindow(this),
                "Вы включаете релейный мост — эта копия браузера станет точкой входа в анонимную сеть для людей из цензурных сетей.\n\n" +
                "Что нужно знать:\n" +
                "• Провайдер увидит трафик анонимной сети с вашей машины (вы НЕ выходной узел — чужой трафик через вас не проходит наружу).\n" +
                "• Без проброса портов 9101–9102 на роутере мост бесполезен для других, но трафик виден. Порт-проброс делается в админке роутера.\n" +
                "• В отдельных странах сам факт работы моста — серая зона. Решайте сами.\n\n" +
                "Включить мост?",
                "Релейный мост", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
            {
                _suppressEvents = true;
                RelayCheck.IsChecked = false;
                _suppressEvents = false;
                return;
            }
            settings.TorRelayAcknowledged = true;
        }

        settings.TorRelayEnabled = enable;
        if (enable && string.IsNullOrWhiteSpace(settings.TorRelayNickname))
            settings.TorRelayNickname = Services.Tor.TorRelayService.DefaultNickname();
        await SettingsService.SaveAsync(settings);
    }

    private async void WebRtc_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents) return;
        var settings = SettingsService.Current;
        settings.PreventWebRtcIpLeak = WebRtcLeakCheck.IsChecked == true;
        await SettingsService.SaveAsync(settings);
    }

    private async void PortShield_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || PortShieldCombo.SelectedItem is not Choice<Services.PortShieldMode> choice)
            return;
        var settings = SettingsService.Current;
        settings.PortShieldMode = choice.Value;
        await SettingsService.SaveAsync(settings);
    }

    // ── Сворачивание ──────────────────────────────────────────────

    private async void Collapse_Click(object sender, RoutedEventArgs e)
    {
        var settings = SettingsService.Current;
        settings.ShowPrivacyMonitor = false;
        await SettingsService.SaveAsync(settings);
        Visibility = Visibility.Collapsed;
        Services.VoiceAssistantService.Announce(
            "Панель приватности скрыта. Вернуть её можно галочкой в настройках.",
            Services.VoiceAnnouncementPriority.Progress);
    }
}
