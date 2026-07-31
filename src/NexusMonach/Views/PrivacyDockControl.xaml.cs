using System.Globalization;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class PrivacyDockControl : UserControl
{
    // ICE собирается строго без STUN/TURN. Запросы во внешнюю сеть не создаются.
    private const string ProbeScript = """
        (async () => {
          const candidates = [];
          try {
            const pc = new RTCPeerConnection({iceServers:[]});
            pc.createDataChannel('local-probe');
            pc.onicecandidate = e => { if (e.candidate) candidates.push(e.candidate.candidate); };
            await pc.setLocalDescription(await pc.createOffer());
            await new Promise(resolve => setTimeout(resolve, 1200));
            pc.close();
          } catch (_) {}
          let canvasToken = '';
          try {
            const c=document.createElement('canvas'); c.width=220; c.height=45;
            const x=c.getContext('2d'); x.font='16px Segoe UI'; x.fillText('Nexus Monach local',5,20);
            canvasToken=c.toDataURL();
          } catch (_) {}
          const data={language:navigator.language||'',timezone:Intl.DateTimeFormat().resolvedOptions().timeZone||'',candidates};
          const source=JSON.stringify(data)+canvasToken+navigator.userAgent+screen.width+'x'+screen.height;
          let hash=2166136261;for(let i=0;i<source.length;i++){hash^=source.charCodeAt(i);hash=Math.imul(hash,16777619);}
          data.fingerprint=(hash>>>0).toString(16).padStart(8,'0').toUpperCase();return data;
        })();
        """;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMinutes(5) };
    private bool _started;
    private bool _refreshing;

    public PrivacyDockControl()
    {
        InitializeComponent();
        _timer.Tick += Timer_Tick;
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;
        if (enabled) await StartAsync(); else _timer.Stop();
    }

    public async Task StartAsync()
    {
        if (_started) { _timer.Start(); await RefreshAsync(); return; }
        _started = true;
        try
        {
            var options = BrowserEnvironment.CreateDiagnosticsControllerOptions();
            await NetworkProbe.EnsureCoreWebView2Async(BrowserEnvironment.Current, options);
            var core = NetworkProbe.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsWebMessageEnabled = false;
            core.SetVirtualHostNameToFolderMapping("nexus.local", AppPaths.WebAssets,
                CoreWebView2HostResourceAccessKind.DenyCors);
            await NavigateLocalAsync();
            _timer.Start();
            await RefreshAsync();
        }
        catch (Exception ex) { ShowError(ex.Message); }
    }

    private async void Control_Loaded(object sender, RoutedEventArgs e)
    {
        if (SettingsService.Current.ShowPrivacyMonitor) await StartAsync();
        else Visibility = Visibility.Collapsed;
    }

    private void Control_Unloaded(object sender, RoutedEventArgs e) => _timer.Stop();
    private async void Timer_Tick(object? sender, EventArgs e) => await RefreshAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Hide_Click(object sender, RoutedEventArgs e) { Visibility = Visibility.Collapsed; _timer.Stop(); }

    public void SetCurrentTransport(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || UrlService.IsInternal(url))
        {
            TransportText.Text = "Транспорт сайта: локальная страница";
            TransportText.Foreground = (Brush)FindResource("MutedTextBrush");
            return;
        }
        var secure = uri.Scheme == Uri.UriSchemeHttps;
        TransportText.Text = secure ? "Транспорт сайта: HTTPS / TLS" : "⚠ Транспорт сайта: HTTP без TLS";
        TransportText.Foreground = secure ? (Brush)FindResource("AccentBrush") : Brushes.OrangeRed;
    }

    private async Task RefreshAsync()
    {
        if (!_started || NetworkProbe.CoreWebView2 is null || _refreshing) return;
        _refreshing = true;
        UpdatedText.Text = "Локальная проверка…";
        try
        {
            if (!NetworkProbe.CoreWebView2.Source.StartsWith("https://nexus.local", StringComparison.OrdinalIgnoreCase))
                await NavigateLocalAsync();
            var probeJson = await NetworkProbe.CoreWebView2.ExecuteScriptAsync(ProbeScript);
            var browser = JsonSerializer.Deserialize<BrowserData>(probeJson, JsonOptions) ?? new BrowserData();
            Render(GetLocalNetworkData(), browser);
            UpdatedText.Text = "Локально " + DateTime.Now.ToString("HH:mm:ss") + " · внешних запросов нет";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _refreshing = false; }
    }

    private void Render(LocalNetworkData network, BrowserData browser)
    {
        BrowserIpText.Text = "Локальный IP: " + Dash(string.Join(", ", network.LocalAddresses.Take(3)));
        DirectIpText.Text = "Маршрут: " + Dash(network.PrimaryInterface);
        LocationText.Text = "Среда: " + network.LocalRegion + " · " + browser.Timezone;
        FlagsText.Text = $"VPN-интерфейс {Mark(network.HasVpnInterface)} · Proxy {Mark(network.HasCustomProxy)} · Tor-порт {Mark(network.HasTorEndpoint)}";

        var candidates = browser.Candidates.Select(ParseCandidate).Where(x => x is not null).Cast<Ice>().ToArray();
        var publicCandidates = candidates.Where(x => IsPublicAddress(x.Address)).Select(x => x.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var localCandidates = candidates.Select(x => x.Address).Where(x => !x.EndsWith(".local", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (publicCandidates.Length > 0)
        {
            WebRtcText.Text = "⚠ WebRTC локально видит публичный адрес: " + string.Join(", ", publicCandidates);
            WebRtcText.Foreground = Brushes.OrangeRed;
        }
        else
        {
            WebRtcText.Text = localCandidates.Length > 0
                ? "WebRTC: только локальные адреса " + string.Join(", ", localCandidates.Take(2))
                : "WebRTC: адреса скрыты mDNS / политикой";
            WebRtcText.Foreground = (Brush)FindResource("MutedTextBrush");
        }

        MatchText.Text = $"Язык {Dash(browser.Language)} · зона {Dash(browser.Timezone)} · " +
                         $"отпечаток {Dash(browser.Fingerprint)}";
        MatchText.Foreground = (Brush)FindResource("AccentBrush");
        DnsText.Text = SecureNetworkConfigurationService.Describe(SettingsService.Current) +
                       " · Windows: " + Dash(string.Join(", ", network.Dns.Take(3)));

        if (publicCandidates.Length > 0)
        {
            RiskText.Text = "WEBRTC ВИДИТ ПУБЛИЧНЫЙ ИНТЕРФЕЙС";
            RiskText.Foreground = Brushes.OrangeRed;
        }
        else if (network.HasTorEndpoint)
        {
            RiskText.Text = "ЛОКАЛЬНЫЙ TOR-ПОРТ ОБНАРУЖЕН";
            RiskText.Foreground = Brushes.DarkOrange;
        }
        else if (network.HasVpnInterface || network.HasCustomProxy)
        {
            RiskText.Text = "ЗАЩИЩЁННЫЙ МАРШРУТ ВИДЕН В WINDOWS";
            RiskText.Foreground = (Brush)FindResource("AccentBrush");
        }
        else
        {
            RiskText.Text = "ЯВНЫХ ЛОКАЛЬНЫХ УТЕЧЕК НЕ НАЙДЕНО";
            RiskText.Foreground = (Brush)FindResource("AccentBrush");
        }
    }

    private async Task NavigateLocalAsync()
    {
        var core = NetworkProbe.CoreWebView2;
        var source = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, CoreWebView2NavigationCompletedEventArgs e) => source.TrySetResult(e);
        core.NavigationCompleted += Handler;
        try
        {
            core.Navigate("https://nexus.local/diagnostics.html");
            var result = await source.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (!result.IsSuccess) throw new InvalidOperationException("Локальная страница диагностики недоступна.");
        }
        finally { core.NavigationCompleted -= Handler; }
    }

    private static LocalNetworkData GetLocalNetworkData()
    {
        var result = new LocalNetworkData();
        try
        {
            var active = NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up &&
                x.NetworkInterfaceType != NetworkInterfaceType.Loopback).ToArray();
            foreach (var adapter in active)
            {
                var properties = adapter.GetIPProperties();
                result.LocalAddresses.AddRange(properties.UnicastAddresses.Where(x => x.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(x => x.Address.ToString()));
                result.Dns.AddRange(properties.DnsAddresses.Select(x => x.ToString()));
                var descriptor = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp ||
                    new[] { "vpn", "wireguard", "wintun", "openvpn", "tap", "tailscale", "zerotier", "adguard" }
                        .Any(descriptor.Contains)) result.HasVpnInterface = true;
                if (string.IsNullOrWhiteSpace(result.PrimaryInterface) && properties.GatewayAddresses.Any(x =>
                        !x.Address.Equals(IPAddress.Any) && !x.Address.Equals(IPAddress.IPv6Any)))
                    result.PrimaryInterface = adapter.Name;
            }
            result.HasTorEndpoint = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(x => IPAddress.IsLoopback(x.Address) && x.Port is 9050 or 9051 or 9150 or 9151);
        }
        catch { }
        result.LocalAddresses = result.LocalAddresses.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.Dns = result.Dns.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        result.HasCustomProxy = SettingsService.Current.EnableCustomProxy;
        try { result.LocalRegion = RegionInfo.CurrentRegion.DisplayName + " (Windows)"; }
        catch { result.LocalRegion = CultureInfo.CurrentCulture.Name + " (Windows)"; }
        return result;
    }

    private static Ice? ParseCandidate(string value)
    {
        var parts = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var typeIndex = Array.IndexOf(parts, "typ");
        return parts.Length > 5 && typeIndex >= 0 && typeIndex + 1 < parts.Length ? new Ice(parts[4], parts[typeIndex + 1]) : null;
    }

    private static bool IsPublicAddress(string value)
    {
        if (!IPAddress.TryParse(value, out var ip)) return false;
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal) return false;
        var bytes = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return !(bytes[0] == 10 || bytes[0] == 127 || bytes[0] == 169 && bytes[1] == 254 ||
                     bytes[0] == 172 && bytes[1] is >= 16 and <= 31 || bytes[0] == 192 && bytes[1] == 168 ||
                     bytes[0] == 100 && bytes[1] is >= 64 and <= 127);
        return !ip.Equals(IPAddress.IPv6Loopback) && (bytes[0] & 0xFE) != 0xFC;
    }

    private void ShowError(string message)
    {
        RiskText.Text = "ЛОКАЛЬНАЯ ДИАГНОСТИКА НЕДОСТУПНА";
        RiskText.Foreground = Brushes.DarkOrange;
        UpdatedText.Text = message;
    }

    private static string Mark(bool value) => value ? "●" : "○";
    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private sealed class LocalNetworkData
    {
        public List<string> LocalAddresses { get; set; } = [];
        public List<string> Dns { get; set; } = [];
        public string PrimaryInterface { get; set; } = string.Empty;
        public string LocalRegion { get; set; } = string.Empty;
        public bool HasVpnInterface { get; set; }
        public bool HasCustomProxy { get; set; }
        public bool HasTorEndpoint { get; set; }
    }

    private sealed class BrowserData
    {
        public string Language { get; set; } = string.Empty;
        public string Timezone { get; set; } = string.Empty;
        public List<string> Candidates { get; set; } = [];
        public string Fingerprint { get; set; } = string.Empty;
    }
    private sealed record Ice(string Address, string Type);
}
