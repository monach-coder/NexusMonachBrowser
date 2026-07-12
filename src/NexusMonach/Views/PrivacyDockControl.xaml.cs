using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
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
    private const string ProbeScript = """
        (async () => {
          const candidates = [];
          try {
            const pc = new RTCPeerConnection({iceServers:[{urls:'stun:stun.cloudflare.com:3478'}]});
            pc.createDataChannel('probe');
            pc.onicecandidate = e => { if (e.candidate) candidates.push(e.candidate.candidate); };
            await pc.setLocalDescription(await pc.createOffer());
            await new Promise(resolve => setTimeout(resolve, 2200));
            pc.close();
          } catch (_) {}
          let canvasToken = '';
          try {
            const c=document.createElement('canvas'); c.width=220; c.height=45;
            const x=c.getContext('2d'); x.font='16px Segoe UI'; x.fillText('Nexus Monach 🛡',5,20);
            canvasToken=c.toDataURL();
          } catch (_) {}
          const data={
            language:navigator.language||'',
            timezone:Intl.DateTimeFormat().resolvedOptions().timeZone||'',
            candidates
          };
          const source=JSON.stringify(data)+canvasToken+navigator.userAgent+screen.width+'x'+screen.height;
          let hash=2166136261;
          for(let i=0;i<source.length;i++){hash^=source.charCodeAt(i);hash=Math.imul(hash,16777619);}
          data.fingerprint=(hash>>>0).toString(16).padStart(8,'0').toUpperCase();
          return data;
        })();
        """;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient DirectClient = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
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
        if (_started)
        {
            _timer.Start();
            await RefreshAsync();
            return;
        }
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
            _timer.Start();
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
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

    private async Task RefreshAsync()
    {
        if (!_started || NetworkProbe.CoreWebView2 is null || _refreshing) return;
        _refreshing = true;
        UpdatedText.Text = "Проверка соединения…";
        try
        {
            var trace = await NavigateAndReadAsync("https://www.cloudflare.com/cdn-cgi/trace");
            var browserIp = ParseTrace(trace).GetValueOrDefault("ip");
            if (!IPAddress.TryParse(browserIp, out _)) throw new InvalidOperationException("Внешний IP не определён.");
            var directIp = await GetDirectIpAsync();

            NetworkData network;
            try
            {
                var json = await NavigateAndReadAsync("https://api.ipapi.is/?q=" + Uri.EscapeDataString(browserIp));
                network = ParseNetwork(json, browserIp, directIp);
            }
            catch { network = new NetworkData { Ip = browserIp, DirectIp = directIp }; }

            await NavigateAsync("https://nexus.local/diagnostics.html");
            var probeJson = await NetworkProbe.CoreWebView2.ExecuteScriptAsync(ProbeScript);
            var probe = JsonSerializer.Deserialize<BrowserData>(probeJson, JsonOptions) ?? new BrowserData();
            Render(network, probe);
            UpdatedText.Text = "Обновлено " + DateTime.Now.ToString("HH:mm:ss") + " · каждые 5 мин";
        }
        catch (Exception ex) { ShowError(ex.Message); }
        finally { _refreshing = false; }
    }

    private void Render(NetworkData network, BrowserData browser)
    {
        BrowserIpText.Text = "IP браузера: " + network.Ip;
        DirectIpText.Text = "Прямой IP: " + Dash(network.DirectIp);
        LocationText.Text = "Регион: " + Dash(string.Join(", ",
            new[] { network.Country, network.City }.Where(x => !string.IsNullOrWhiteSpace(x))));
        FlagsText.Text = network.HasReputation
            ? $"VPN {Mark(network.IsVpn)} · Proxy {Mark(network.IsProxy)} · Tor {Mark(network.IsTor)} · Hosting {Mark(network.IsDatacenter)}"
            : "VPN/Proxy/Tor/Hosting: нет данных";

        var publicWebRtc = browser.Candidates.Select(ParseCandidate)
            .Where(x => x is { Type: "srflx" }).Select(x => x!.Address)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var mismatch = publicWebRtc.Any(x => !x.Equals(network.Ip, StringComparison.OrdinalIgnoreCase));
        WebRtcText.Text = mismatch ? "⚠ WebRTC: другой IP " + string.Join(", ", publicWebRtc)
            : publicWebRtc.Count > 0 ? "WebRTC: совпадает" : "WebRTC: публичный IP не раскрыт";
        WebRtcText.Foreground = mismatch ? Brushes.OrangeRed : (Brush)FindResource("MutedTextBrush");

        var timezoneMatch = network.Timezone.Equals(browser.Timezone, StringComparison.OrdinalIgnoreCase) &&
                            !string.IsNullOrWhiteSpace(network.Timezone);
        var region = browser.Language.Split('-', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        var languageMatch = region?.Equals(network.CountryCode, StringComparison.OrdinalIgnoreCase) == true;
        MatchText.Text = $"Часовой пояс {Mark(timezoneMatch)} · язык/регион {Mark(languageMatch)}";
        MatchText.Foreground = timezoneMatch && languageMatch ? (Brush)FindResource("AccentBrush") : Brushes.DarkOrange;
        DnsText.Text = "DNS Windows: " + Dash(string.Join(", ", GetDns()));
        FingerprintText.Text = "Отпечаток: " + Dash(browser.Fingerprint);

        if (network.IsTor || mismatch)
        {
            RiskText.Text = network.IsTor ? "TOR ОБНАРУЖЕН" : "ВОЗМОЖНА УТЕЧКА WEBRTC";
            RiskText.Foreground = Brushes.OrangeRed;
        }
        else if (!network.HasReputation)
        {
            RiskText.Text = "РЕПУТАЦИЯ IP НЕДОСТУПНА";
            RiskText.Foreground = Brushes.DarkOrange;
        }
        else if (network.IsVpn || network.IsProxy || network.IsDatacenter)
        {
            RiskText.Text = "VPN / PROXY ВИДЕН САЙТАМ";
            RiskText.Foreground = Brushes.DarkOrange;
        }
        else
        {
            RiskText.Text = "ЯВНЫЕ СЕТЕВЫЕ МЕТКИ НЕ НАЙДЕНЫ";
            RiskText.Foreground = (Brush)FindResource("AccentBrush");
        }
    }

    private async Task<string> NavigateAndReadAsync(string url)
    {
        await NavigateAsync(url);
        var value = await NetworkProbe.CoreWebView2.ExecuteScriptAsync("document.body?.innerText || ''");
        return JsonSerializer.Deserialize<string>(value) ?? string.Empty;
    }

    private async Task NavigateAsync(string url)
    {
        var core = NetworkProbe.CoreWebView2;
        var source = new TaskCompletionSource<CoreWebView2NavigationCompletedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? _, CoreWebView2NavigationCompletedEventArgs e) => source.TrySetResult(e);
        core.NavigationCompleted += Handler;
        try
        {
            core.Navigate(url);
            var result = await source.Task.WaitAsync(TimeSpan.FromSeconds(25));
            if (!result.IsSuccess) throw new InvalidOperationException("Сетевая ошибка: " + result.WebErrorStatus);
        }
        finally { core.NavigationCompleted -= Handler; }
    }

    private static Dictionary<string, string> ParseTrace(string value) => value
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(x => x.Split('=', 2)).Where(x => x.Length == 2)
        .ToDictionary(x => x[0], x => x[1], StringComparer.OrdinalIgnoreCase);

    private static NetworkData ParseNetwork(string json, string ip, string directIp)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new NetworkData
        {
            HasReputation = true, Ip = Text(root, "ip") ?? ip, DirectIp = directIp,
            IsVpn = Bool(root, "is_vpn"), IsProxy = Bool(root, "is_proxy"),
            IsTor = Bool(root, "is_tor"), IsDatacenter = Bool(root, "is_datacenter"),
            Country = Nested(root, "location", "country") ?? string.Empty,
            CountryCode = Nested(root, "location", "country_code") ?? string.Empty,
            City = Nested(root, "location", "city") ?? string.Empty,
            Timezone = Nested(root, "location", "timezone") ?? string.Empty
        };
    }

    private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var x) ? x.ToString() : null;
    private static bool Bool(JsonElement root, string name) => root.TryGetProperty(name, out var x) && x.ValueKind == JsonValueKind.True;
    private static string? Nested(JsonElement root, string parent, string name) =>
        root.TryGetProperty(parent, out var x) && x.ValueKind == JsonValueKind.Object &&
        x.TryGetProperty(name, out var y) ? y.ToString() : null;

    private static Ice? ParseCandidate(string value)
    {
        var p = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var i = Array.IndexOf(p, "typ");
        return p.Length > 5 && i >= 0 && i + 1 < p.Length ? new Ice(p[4], p[i + 1]) : null;
    }

    private static async Task<string> GetDirectIpAsync()
    {
        try
        {
            var value = await DirectClient.GetStringAsync("https://www.cloudflare.com/cdn-cgi/trace");
            var ip = ParseTrace(value).GetValueOrDefault("ip");
            return IPAddress.TryParse(ip, out _) ? ip : string.Empty;
        }
        catch { return string.Empty; }
    }

    private static List<string> GetDns()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Where(x => x.OperationalStatus == OperationalStatus.Up)
                .SelectMany(x => x.GetIPProperties().DnsAddresses).Select(x => x.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        catch { return []; }
    }

    private void ShowError(string message)
    {
        RiskText.Text = "ДИАГНОСТИКА НЕДОСТУПНА";
        RiskText.Foreground = Brushes.DarkOrange;
        UpdatedText.Text = message;
    }
    private static string Mark(bool value) => value ? "●" : "○";
    private static string Dash(string? value) => string.IsNullOrWhiteSpace(value) ? "—" : value;

    private sealed class NetworkData
    {
        public bool HasReputation { get; init; }
        public string Ip { get; init; } = string.Empty;
        public string DirectIp { get; init; } = string.Empty;
        public bool IsVpn { get; init; }
        public bool IsProxy { get; init; }
        public bool IsTor { get; init; }
        public bool IsDatacenter { get; init; }
        public string Country { get; init; } = string.Empty;
        public string CountryCode { get; init; } = string.Empty;
        public string City { get; init; } = string.Empty;
        public string Timezone { get; init; } = string.Empty;
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
