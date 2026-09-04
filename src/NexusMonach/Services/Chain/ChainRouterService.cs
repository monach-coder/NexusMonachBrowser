using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NexusMonach.Services.Chain;

/// <summary>Куда отправить очередное соединение вкладок.</summary>
public enum ChainRoute
{
    /// <summary>Напрямую, без туннелей — под защитой Режима Следа и щитов.</summary>
    Direct,
    /// <summary>Через анонимный слой (возможен только в обёртке туннеля).</summary>
    Tor,
    /// <summary>Через транспорт сервера (Xray).</summary>
    Transport,
    /// <summary>Через ручной прокси из настроек.</summary>
    CustomProxy
}

/// <summary>
/// Встроенный маршрутизатор цепочки: локальный SOCKS5-сервер на loopback,
/// через который проходят соединения вкладок. Прокси зашивается в аргументы
/// окружения WebView2 один раз — а решение о маршруте принимается заново
/// для КАЖДОГО соединения по живому состоянию цепочки. Поэтому обрыв VPN
/// посреди сессии больше не оставляет браузер без сети: новые соединения
/// уходят напрямую с защитой Режима Следа, а вернувшийся туннель подхватывается
/// без перезапуска. Мёртвый туннель распознаётся по ошибке соединения и
/// озвучивается.
/// </summary>
public static class ChainRouterService
{
    private static TcpListener? _listener;
    private static readonly object Sync = new();
    private static volatile bool _fallbackAnnounced;
    private static DateTimeOffset _lastFallbackAnnounceUtc = DateTimeOffset.MinValue;
    // Живые туннели вкладок: при смене маршрута их можно разорвать, чтобы
    // движок не катался по старым keep-alive сокетам минутами.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<
        Guid, (ChainRoute Route, TcpClient Client, Stream Upstream)> Tunnels = new();

    public static int Port { get; private set; }
    public static bool IsRunning => _listener is not null;

    /// <summary>
    /// Разрывает все живые туннели вкладок. Вызывается при переключении
    /// тумблеров маршрута: движок держит пул соединений, и без разрыва
    /// страницы ещё долго едут по старому маршруту, как бы «переключение
    /// не сработало». Обрыв безвреден: движок тут же открывает свежие
    /// сокеты — уже по новому маршруту.
    /// </summary>
    public static void DropAllTunnels()
    {
        foreach (var (_, tunnel) in Tunnels)
        {
            try { tunnel.Client.Close(); } catch { }
            try { tunnel.Upstream.Close(); } catch { }
        }
    }

    /// <summary>Запускает маршрутизатор до создания окружения WebView2.</summary>
    public static void Start()
    {
        lock (Sync)
        {
            if (_listener is not null) return;
            for (var attempt = 0; attempt < 16; attempt++)
            {
                var port = RandomNumberGenerator.GetInt32(9700, 9990);
                var candidate = new TcpListener(IPAddress.Loopback, port);
                try
                {
                    candidate.Start(64);
                    _listener = candidate;
                    Port = port;
                    break;
                }
                catch (SocketException)
                {
                    candidate.Stop();
                }
            }
            if (_listener is null) return;
            _ = Task.Run(AcceptLoopAsync);
        }
    }

    /// <summary>Останавливает маршрутизатор (выход браузера).</summary>
    public static void Stop()
    {
        lock (Sync)
        {
            _listener?.Stop();
            _listener = null;
        }
    }

    private static async Task AcceptLoopAsync()
    {
        var listener = _listener;
        if (listener is null) return;
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (SocketException) { return; }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    /// <summary>
    /// Живое состояние обёртки Тора: процесс крутится и (есть туннель
    /// ИЛИ настроены мосты — webtunnel сам пробивает путь под HTTPS, ему
    /// обёртка не обязательна). Финальное слово за пробой цепочки: SOCKS
    /// отвечает, но без работоспособных цепочек каждый запрос гнил в нём
    /// по 15 секунд до запасного хода. Здоровье проверяется реальным
    /// CONNECT через SOCKS Тора и кэшируется на полминуты.
    /// </summary>
    internal static bool IsTorWrapped() =>
        Services.Tor.TorService.IsRunning &&
        (Services.Vless.VlessRuntime.IsRunning ||
         Services.Warp.WarpService.IsConnected ||
         Services.Tor.VpnDetector.DetectCached().VpnActive ||
         HasBridges()) &&
        TorRouteHealthy();

    private static bool HasBridges()
    {
        var settings = SettingsService.Current;
        return (!string.IsNullOrWhiteSpace(settings.TorCustomBridges) ||
                !string.IsNullOrWhiteSpace(settings.TorBridgePool)) &&
            // Транспорт моста обязан жить на машине: без клиента строка —
            // не повод пускать вкладки в слой.
            System.IO.Directory.Exists(
                System.IO.Path.Combine(AppContext.BaseDirectory, "tor"));
    }

    private static volatile bool _torHealthy;
    private static DateTimeOffset _torProbedAt = DateTimeOffset.MinValue;
    private static readonly SemaphoreSlim TorProbeGate = new(1, 1);

    private static bool TorRouteHealthy()
    {
        var probed = _torProbedAt;
        if (DateTimeOffset.UtcNow - probed < TimeSpan.FromSeconds(30))
            return _torHealthy;
        // Проба уходит в фон; до первого ответа считаем слой неготовым —
        // вкладки не должны гнить в необстрелянном SOCKS.
        _ = Task.Run(ProbeTorRouteAsync);
        return probed == DateTimeOffset.MinValue ? false : _torHealthy;
    }

    /// <summary>Проба слоя: реальный CONNECT через SOCKS Тора к 1.1.1.1.</summary>
    private static async Task ProbeTorRouteAsync()
    {
        if (!await TorProbeGate.WaitAsync(0)) return;
        try
        {
            var healthy = false;
            try
            {
                var upstream = await ConnectSocksUpstreamAsync(
                    "127.0.0.1", Services.Tor.TorService.SocksPort, "1.1.1.1", 443);
                upstream.Dispose();
                healthy = true;
            }
            catch { healthy = false; }
            _torHealthy = healthy;
            _torProbedAt = DateTimeOffset.UtcNow;
            if (!healthy)
                CrashReportService.AddBreadcrumb("chain-router", "tor-route-unhealthy");
        }
        finally
        {
            TorProbeGate.Release();
        }
    }

    internal static ChainRoute PickRoute(
        bool torInChain, bool torWrapped,
        bool vlessEnabled, bool vlessRunning,
        bool customProxyEnabled) =>
        torInChain && torWrapped ? ChainRoute.Tor :
        vlessEnabled && vlessRunning ? ChainRoute.Transport :
        customProxyEnabled ? ChainRoute.CustomProxy :
        ChainRoute.Direct;

    private static ChainRoute CurrentRoute()
    {
        var settings = SettingsService.Current;
        return PickRoute(
            settings.TorInChain, IsTorWrapped(),
            settings.VlessEnabled, Services.Vless.VlessRuntime.IsRunning,
            settings.EnableCustomProxy);
    }

    /// <summary>Временная диагностика живой машины — в файл рядом с профилем.</summary>
    internal static void RouterDebugLog(string line)
    {
        try
        {
            File.AppendAllText(
                Path.Combine(Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData), "NexusMonach", "router-debug.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {line}{Environment.NewLine}");
        }
        catch { }
    }

    private static async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();

                // 1. Приветствие SOCKS5: принимаем «без аутентификации».
                var greeting = new byte[64];
                var greetingLength = await ReadAsync(stream, greeting, 2, TimeSpan.FromSeconds(10));
                if (greetingLength < 2 || greeting[0] != 5 || greeting[1] == 0) return;
                var methods = new byte[greeting[1]];
                if (await ReadAsync(stream, methods, methods.Length, TimeSpan.FromSeconds(10)) < methods.Length)
                    return;
                await stream.WriteAsync(new byte[] { 5, 0 });

                // 2. Запрос CONNECT: [VER, CMD, RSV, ATYP, адрес, порт].
                // Для IPv4/IPv6 пятый байт — первый байт адреса; длину
                // содержит только доменная запись.
                var head = new byte[5];
                if (await ReadAsync(stream, head, head.Length, TimeSpan.FromSeconds(10)) < 5 ||
                    head[0] != 5 || head[1] != 1)
                    return;
                byte[] addressBytes;
                switch (head[3])
                {
                    case 1: // IPv4
                        addressBytes = new byte[4];
                        addressBytes[0] = head[4];
                        break;
                    case 3: // домен — разрешает верхний прокси (без локальной утечки DNS)
                        addressBytes = new byte[head[4]];
                        if (addressBytes.Length is < 1 or > 253) return;
                        break;
                    case 4: // IPv6
                        addressBytes = new byte[16];
                        addressBytes[0] = head[4];
                        break;
                    default:
                        return;
                }
                // Для IPv4/IPv6 пятый байт запроса — первый байт адреса;
                // для домена — ДЛИНА имени, и само имя читается целиком
                // со следующего байта (классическая ловушка SOCKS5).
                var pending = head[3] == 3 ? addressBytes.Length : addressBytes.Length - 1;
                if (pending > 0)
                {
                    var rest = new byte[pending];
                    if (await ReadAsync(stream, rest, pending, TimeSpan.FromSeconds(10)) < pending)
                        return;
                    Array.Copy(rest, 0, addressBytes, head[3] == 3 ? 0 : 1, pending);
                }
                var portBytes = new byte[2];
                if (await ReadAsync(stream, portBytes, 2, TimeSpan.FromSeconds(10)) < 2) return;
                var port = portBytes[0] << 8 | portBytes[1];
                var host = head[3] == 3
                    ? Encoding.ASCII.GetString(addressBytes)
                    : new IPAddress(addressBytes).ToString();

                // Петля маршрутизатора — отказ без выхода в сеть.
                if (IPAddress.TryParse(host, out var parsed) &&
                    IPAddress.IsLoopback(parsed) && port == Port)
                    return;

                var route = CurrentRoute();
                Stream? upstream;
                try
                {
                    upstream = await ConnectUpstreamAsync(route, host, port);
                }
                catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException)
                {
                    upstream = null;
                }

                if (upstream is null && route != ChainRoute.Direct)
                {
                    // Туннель мёртв (обрыв VPN, упавший транспорт) — прямой
                    // ход с защитой Режима Следа вместо чёрной дыры.
                    AnnounceFallback(route);
                    try { upstream = await ConnectUpstreamAsync(ChainRoute.Direct, host, port); }
                    catch { /* и прямой не прошёл — честный отказ ниже */ }
                }
                if (upstream is null)
                {
                    // Явный отказ SOCKS5 вместо тихого обрыва: вкладка сразу
                    // показывает ошибку, а не висит до таймаута движка.
                    await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 0, 0, 0, 0, 0, 0 });
                    return;
                }

                // Сначала регистрация, потом ответ «успех»: получив успех,
                // клиент обязан попасть под DropAllTunnels — иначе смена
                // маршрута может проскочить мимо только что поднятого туннеля.
                var tunnelId = Guid.NewGuid();
                Tunnels[tunnelId] = (route, client, upstream);
                try
                {
                    await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
                    await PumpAsync(stream, upstream);
                }
                finally
                {
                    Tunnels.TryRemove(tunnelId, out _);
                }
            }
            catch
            {
                // Обрыв клиента — штатная ситуация прокси.
            }
        }
    }

    private static async Task<Stream> ConnectUpstreamAsync(ChainRoute route, string host, int port)
    {
        switch (route)
        {
            case ChainRoute.Tor:
                return await ConnectSocksUpstreamAsync(
                    "127.0.0.1", Services.Tor.TorService.SocksPort, host, port);
            case ChainRoute.Transport:
                return await ConnectSocksUpstreamAsync(
                    "127.0.0.1", Services.Vless.VlessRuntime.SocksPort, host, port);
            case ChainRoute.CustomProxy:
            {
                var settings = SettingsService.Current;
                if (settings.ProxyKind == Models.ProxyKind.Socks5)
                    return await ConnectSocksUpstreamAsync(settings.ProxyHost, settings.ProxyPort, host, port);
                return await ConnectHttpUpstreamAsync(settings.ProxyHost, settings.ProxyPort, host, port);
            }
            default:
            {
                // Прямое соединение: домен разрешается лестницей — системный
                // резолвер с коротким окном, затем DoH по IP-литералу (локальный
                // DNS вообще не участвует; заодно нет утечки DNS провайдеру).
                var addresses = await ResolveHostCachedAsync(host);
                if (addresses.Length == 0) throw new SocketException((int)SocketError.HostNotFound);
                TcpClient? connected = null;
                foreach (var address in addresses)
                {
                    var candidate = new TcpClient();
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await candidate.ConnectAsync(address, port, timeout.Token);
                        connected = candidate;
                        break;
                    }
                    catch
                    {
                        candidate.Dispose();
                    }
                }
                if (connected is null)
                    throw new SocketException((int)SocketError.ConnectionRefused);
                connected.NoDelay = true;
                return connected.GetStream();
            }
        }
    }

    /// <summary>
    /// DNS-кэш роутера: страница — это 15–40 хостов, и без кэша каждый
    /// повторный визит платил за DNS ту же цену, что первый, а параллельные
    /// соединения на один хост гоняли лестницу каждый сам за себя.
    /// Успех кэшируется на 2 минуты, отказ — на 15 секунд (мгновенный
    /// повторный отказ вместо шестикратного перебора этажей); запросы
    /// одного хоста дедуплицируются в один полёт.
    /// </summary>
    private sealed record DnsAnswer(IPAddress[] Addresses, DateTimeOffset ExpiryUtc, bool Negative);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DnsAnswer> DnsCache =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Task<IPAddress[]>> DnsInFlight =
        new(StringComparer.OrdinalIgnoreCase);

    private static Task<IPAddress[]> ResolveHostCachedAsync(string host)
    {
        if (IPAddress.TryParse(host, out var literal))
            return Task.FromResult(new[] { literal });
        var now = DateTimeOffset.UtcNow;
        if (DnsCache.TryGetValue(host, out var cached))
        {
            if (cached.ExpiryUtc > now)
            {
                if (cached.Negative)
                    return Task.FromException<IPAddress[]>(
                        new SocketException((int)SocketError.HostNotFound));
                return Task.FromResult(cached.Addresses);
            }
            DnsCache.TryRemove(host, out _);
        }
        return DnsInFlight.GetOrAdd(host, _ => ResolveAndCacheAsync(host));
    }

    private static async Task<IPAddress[]> ResolveAndCacheAsync(string host)
    {
        try
        {
            var addresses = await ResolveHostAsync(host);
            DnsCache[host] = new DnsAnswer(addresses, DateTimeOffset.UtcNow.AddSeconds(120), Negative: false);
            return addresses;
        }
        catch
        {
            DnsCache[host] = new DnsAnswer([], DateTimeOffset.UtcNow.AddSeconds(15), Negative: true);
            throw;
        }
        finally
        {
            DnsInFlight.TryRemove(host, out _);
        }
    }

    /// <summary>
    /// Лестница разрешения имён для прямого маршрута — с забегом на старте.
    /// Системный резолвер и DoH стартуют ОДНОВРЕМЕННО: на здоровой машине
    /// система отвечает мгновенно и DoH тихо завершается вхолостую; когда
    /// локальный DNS перехвачен или душится VPN-фильтром, DoH вырывается
    /// вперёд, не выжидая двухсекундного окна первого этажа. Следующие
    /// этажи (TCP53 → UDP53 → DoH фильтра → медленный системный) идут
    /// последовательно, как раньше.
    /// </summary>
    private static async Task<IPAddress[]> ResolveHostAsync(string host)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];

        var dohTask = ResolveViaDohAsync(host);
        try
        {
            using var window = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var all = await Dns.GetHostAddressesAsync(host).WaitAsync(window.Token);
            RouterDebugLog($"dns system ok: {string.Join(',', all.Select(a => a.ToString()))}");
            var ipv4 = all.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToArray();
            if (ipv4.Length > 0) return ipv4;
            if (all.Length > 0) return all;
        }
        catch (Exception ex) { RouterDebugLog("dns system fail: " + ex.GetType().Name); }
        var viaDoh = await dohTask;
        if (viaDoh.Length > 0)
        {
            CrashReportService.AddBreadcrumb("chain-router", "dns-via-doh");
            return viaDoh;
        }
        RouterDebugLog("doh: все эндпоинты молчат");
        // DNS-over-TCP: обычный TCP-порт 53 — работает там, где локальный
        // фильтр душит и системный DNS, и DoH (443 к резолверам), но не
        // трогает прямые TCP-соединения самого браузера.
        var viaTcp = await ResolveViaTcpDnsAsync(host);
        if (viaTcp.Length > 0)
        {
            CrashReportService.AddBreadcrumb("chain-router", "dns-via-tcp53");
            return viaTcp;
        }
        RouterDebugLog("tcp53: все серверы молчат");
        // UDP-запрос классического DNS: последний сетевой ход перед честным
        // отказом — некоторые фильтры душат только TCP/DoH.
        var viaUdp = await ResolveViaUdpDnsAsync(host);
        if (viaUdp.Length > 0)
        {
            CrashReportService.AddBreadcrumb("chain-router", "dns-via-udp53");
            return viaUdp;
        }
        // Джода против per-app DNS-фильтров: DoH к СОБСТВЕННОМУ резолверу
        // фильтра (dns.adguard.com через его anycast-IP, SNI его же имени).
        // Свой DNS фильтр не блокирует, а 443/TCP к произвольному IP у
        // браузера работает.
        var viaFilterOwn = await ResolveViaFilterOwnDohAsync(host);
        if (viaFilterOwn.Length > 0)
        {
            CrashReportService.AddBreadcrumb("chain-router", "dns-via-filter-own-doh");
            return viaFilterOwn;
        }
        RouterDebugLog("filter-own doh: молчит");
        // Последний шанс: медленный системный резолвер — некоторые локальные
        // перехватчики DNS отвечают, но дольше первого окна.
        try
        {
            using var slow = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var viaSystem = await Dns.GetHostAddressesAsync(host).WaitAsync(slow.Token);
            var ipv4 = viaSystem
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                .ToArray();
            if (ipv4.Length > 0)
            {
                CrashReportService.AddBreadcrumb("chain-router", "dns-via-system-slow");
                return ipv4;
            }
            if (viaSystem.Length > 0) return viaSystem;
        }
        catch { /* совсем тихо — честный отказ */ }
        throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>
    /// DoH-резолверы по IP-литералам. Не один и не три: локальные фильтры
    /// (например, VPN-клиенты) душат DNS по-разному — блок-листами «известных»
    /// DoH-адресов, перехватом 53/TCP+UDP по приложениям. Поэтому в лестнице
    /// и первичные, и ВТОРИЧНЫЕ IP тех же резолверов (1.0.0.1, 8.8.4.4,
    /// 149.112.112.112): их в блок-листах часто нет, а сертификаты покрывают.
    /// </summary>
    internal static readonly string[] DohEndpoints =
    [
        "https://1.0.0.1/dns-query",
        "https://8.8.8.8/dns-query",
        "https://8.8.4.4/dns-query",
        "https://1.1.1.1/dns-query",
        "https://149.112.112.112/dns-query"
    ];

    /// <summary>DoH-клиент без системного прокси: адрес — IP-литерал.</summary>
    private static readonly HttpClient DohClient = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static async Task<IPAddress[]> ResolveViaDohAsync(string host)
    {
        foreach (var endpoint in DohEndpoints)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    endpoint + "?name=" + Uri.EscapeDataString(host) + "&type=A");
                request.Headers.Accept.ParseAdd("application/dns-json");
                using var response = await DohClient.SendAsync(request);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync();
                var addresses = ParseDohIpv4(json).Select(IPAddress.Parse).ToArray();
                if (addresses.Length > 0) return addresses;
            }
            catch
            {
                // Этот резолвер перехвачен или молчит — пробуем следующий.
            }
        }
        return [];
    }

    /// <summary>
    /// DNS-over-TCP: первичные и вторичные IP резолверов на порт 53 —
    /// обычный TCP мимо блок-листов DoH-портов.
    /// </summary>
    internal static readonly string[] TcpDnsServers =
    [
        "1.0.0.1", "8.8.8.8", "8.8.4.4", "1.1.1.1", "149.112.112.112"
    ];

    private static async Task<IPAddress[]> ResolveViaTcpDnsAsync(string host)
    {
        foreach (var server in TcpDnsServers)
        {
            try
            {
                using var tcp = new TcpClient();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1.5));
                await tcp.ConnectAsync(server, 53, timeout.Token);
                tcp.NoDelay = true;
                var query = BuildDnsQuery(host);
                var stream = tcp.GetStream();
                var framed = new byte[query.Length + 2];
                framed[0] = (byte)(query.Length >> 8);
                framed[1] = (byte)(query.Length & 0xFF);
                query.CopyTo(framed, 2);
                await stream.WriteAsync(framed);
                var lengthHeader = new byte[2];
                if (await ReadAsync(stream, lengthHeader, 2, TimeSpan.FromSeconds(1.5)) < 2)
                    continue;
                var responseLength = lengthHeader[0] << 8 | lengthHeader[1];
                if (responseLength is < 12 or > 4096) continue;
                var response = new byte[responseLength];
                if (await ReadAsync(stream, response, responseLength, TimeSpan.FromSeconds(1.5)) <
                    responseLength) continue;
                var addresses = ParseDnsA(response);
                if (addresses.Count > 0)
                    return addresses.Select(IPAddress.Parse).ToArray();
            }
            catch
            {
                // Этот сервер молчит — следующий.
            }
        }
        return [];
    }

    /// <summary>
    /// DoH через резолвер самого фильтра: dns.adguard.com по anycast-адресу.
    /// ConnectCallback сам владеет сокетом — системный DNS не участвует.
    /// </summary>
    private static readonly HttpClient FilterOwnDohClient = new(
        new SocketsHttpHandler
        {
            UseProxy = false,
            ConnectCallback = async (context, token) =>
            {
                var tcp = new TcpClient();
                await tcp.ConnectAsync(IPAddress.Parse("94.140.14.14"), 443, token);
                var ssl = new System.Net.Security.SslStream(tcp.GetStream());
                await ssl.AuthenticateAsClientAsync(
                    new System.Net.Security.SslClientAuthenticationOptions
                    {
                        TargetHost = context.DnsEndPoint.Host
                    }, token);
                return ssl;
            }
        })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static async Task<IPAddress[]> ResolveViaFilterOwnDohAsync(string host)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://dns.adguard.com/dns-query?name=" + Uri.EscapeDataString(host) + "&type=A");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await FilterOwnDohClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            var addresses = ParseDohIpv4(json).Select(IPAddress.Parse).ToArray();
            if (addresses.Length > 0) return addresses;
        }
        catch
        {
            // Фильтр чужой или суровее обычного — честный отказ дальше.
        }
        return [];
    }

    /// <summary>Классический UDP DNS к тем же резолверам: последний ход.</summary>
    private static async Task<IPAddress[]> ResolveViaUdpDnsAsync(string host)
    {
        foreach (var server in TcpDnsServers)
        {
            try
            {
                using var udp = new System.Net.Sockets.UdpClient();
                udp.Client.ReceiveTimeout = 1500;
                udp.Connect(server, 53);
                var query = BuildDnsQuery(host);
                await udp.SendAsync(query, query.Length);
                var result = await udp.ReceiveAsync();
                var addresses = ParseDnsA(result.Buffer);
                if (addresses.Count > 0)
                    return addresses.Select(IPAddress.Parse).ToArray();
            }
            catch
            {
                // Следующий сервер.
            }
        }
        return [];
    }

    /// <summary>DNS-запрос A-записи: заголовок + вопрос, без сжатия.</summary>
    internal static byte[] BuildDnsQuery(string host)
    {
        var labels = host.Split('.');
        using var question = new MemoryStream();
        foreach (var label in labels)
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            question.WriteByte((byte)bytes.Length);
            question.Write(bytes, 0, bytes.Length);
        }
        question.WriteByte(0);
        var tail = new byte[] { 0, 1, 0, 1 }; // A, IN
        var query = new byte[12 + (int)question.Length + tail.Length];
        query[0] = 0x12; query[1] = 0x34;      // идентификатор
        query[2] = 0x01; query[3] = 0x00;      // рекурсия желательна
        query[5] = 0x01;                       // один вопрос
        var offset = 12;
        question.ToArray().CopyTo(query, offset);
        offset += (int)question.Length;
        tail.CopyTo(query, offset);
        return query;
    }

    /// <summary>
    /// Разбор ответа DNS: только A-записи (IPv4). Имена пропускаются с
    /// учётом сжатия (указатели 0xC0), CNAME — мимо. Чистая функция для тестов.
    /// </summary>
    internal static IReadOnlyList<string> ParseDnsA(byte[] response)
    {
        var result = new List<string>();
        if (response.Length < 12) return result;
        var questions = response[4] << 8 | response[5];
        var answers = response[6] << 8 | response[7];
        var offset = 12;
        for (var i = 0; i < questions && offset < response.Length; i++)
            offset = SkipDnsName(response, offset);
        offset += 4; // тип + класс вопроса
        for (var i = 0; i < answers && offset < response.Length; i++)
        {
            offset = SkipDnsName(response, offset);
            if (offset + 10 > response.Length) break;
            var type = response[offset] << 8 | response[offset + 1];
            var dataLength = response[offset + 8] << 8 | response[offset + 9];
            offset += 10;
            if (type == 1 && dataLength == 4 && offset + 4 <= response.Length)
                result.Add($"{response[offset]}.{response[offset + 1]}.{response[offset + 2]}.{response[offset + 3]}");
            offset += dataLength;
        }
        return result;
    }

    /// <summary>Пропуск имени с учётом сжатия: метки или указатель 0xC0.</summary>
    private static int SkipDnsName(byte[] message, int offset)
    {
        while (offset < message.Length)
        {
            var length = message[offset];
            if (length == 0) return offset + 1;
            if ((length & 0xC0) == 0xC0) return offset + 2;
            offset += 1 + length;
        }
        return offset;
    }

    /// <summary>Разбор JSON-ответа DoH: записи типа A (1) → IPv4. Чистая функция для тестов.</summary>
    internal static IReadOnlyList<string> ParseDohIpv4(string json)
    {
        var result = new List<string>();
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("Answer", out var answers) ||
                answers.ValueKind != System.Text.Json.JsonValueKind.Array)
                return result;
            foreach (var answer in answers.EnumerateArray())
            {
                if (!answer.TryGetProperty("type", out var type) || type.GetInt32() != 1)
                    continue;
                if (answer.TryGetProperty("data", out var data) &&
                    data.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var value = data.GetString();
                    if (!string.IsNullOrEmpty(value) && IPAddress.TryParse(value, out var parsed) &&
                        parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        result.Add(value);
                }
            }
        }
        catch
        {
            // Битый JSON — просто пустой результат.
        }
        return result;
    }

    /// <summary>Клиентская сторона SOCKS5: приветствие + CONNECT за домен.</summary>
    private static async Task<Stream> ConnectSocksUpstreamAsync(
        string proxyHost, int proxyPort, string targetHost, int targetPort)
    {
        var upstream = new TcpClient();
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await upstream.ConnectAsync(proxyHost, proxyPort, connectTimeout.Token);
        upstream.NoDelay = true;
        var stream = upstream.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 });
        var reply = new byte[2];
        if (await ReadAsync(stream, reply, 2, TimeSpan.FromSeconds(10)) < 2 ||
            reply[0] != 5 || reply[1] != 0)
            throw new IOException("Верхний SOCKS5 отклонил аутентификацию.");

        var request = BuildConnectRequest(targetHost, targetPort);
        await stream.WriteAsync(request);
        var head = new byte[4];
        if (await ReadAsync(stream, head, 4, TimeSpan.FromSeconds(15)) < 4 || head[1] != 0)
            throw new IOException("Верхний SOCKS5 отклонил соединение.");
        // Ответ: [VER, REP, RSV, ATYP] + адрес привязки + порт. Адрес не нужен
        // для работы — обязан быть корректно прочитан, чтобы не остаться в буфере.
        var boundLength = head[3] switch
        {
            1 => 4,
            4 => 16,
            3 => -1, // сначала байт длины домена
            _ => 0
        };
        if (boundLength == -1)
        {
            var lengthByte = new byte[1];
            if (await ReadAsync(stream, lengthByte, 1, TimeSpan.FromSeconds(10)) < 1)
                throw new IOException("Верхний SOCKS5 оборвался.");
            boundLength = lengthByte[0];
        }
        if (boundLength > 0)
        {
            var bound = new byte[boundLength + 2];
            if (await ReadAsync(stream, bound, bound.Length, TimeSpan.FromSeconds(10)) < bound.Length)
                throw new IOException("Верхний SOCKS5 оборвался.");
        }
        return stream;
    }

    private static async Task<Stream> ConnectHttpUpstreamAsync(
        string proxyHost, int proxyPort, string targetHost, int targetPort)
    {
        var upstream = new TcpClient();
        using var connectTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await upstream.ConnectAsync(proxyHost, proxyPort, connectTimeout.Token);
        upstream.NoDelay = true;
        var stream = upstream.GetStream();
        var authority = targetHost.Contains(':', StringComparison.Ordinal)
            ? "[" + targetHost + "]:" + targetPort
            : targetHost + ":" + targetPort;
        var request = Encoding.ASCII.GetBytes(
            "CONNECT " + authority + " HTTP/1.1\r\nHost: " + authority + "\r\n\r\n");
        await stream.WriteAsync(request);
        var header = new byte[1024];
        var total = 0;
        while (total < header.Length)
        {
            var read = await stream.ReadAsync(header.AsMemory(total, header.Length - total));
            if (read <= 0) break;
            total += read;
            if (Encoding.ASCII.GetString(header, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                break;
        }
        var status = Encoding.ASCII.GetString(header, 0, Math.Max(total, 12));
        if (!status.StartsWith("HTTP/1.", StringComparison.Ordinal) ||
            !status.Contains(" 200", StringComparison.Ordinal))
            throw new IOException("HTTP-прокси отклонил CONNECT: " + status.Split('\r')[0]);
        return stream;
    }

    /// <summary>Разбор домена/IP в запрос CONNECT для верхнего SOCKS5.</summary>
    internal static byte[] BuildConnectRequest(string host, int port)
    {
        if (IPAddress.TryParse(host, out var address))
        {
            var bytes = address.GetAddressBytes();
            var isV6 = bytes.Length == 16;
            var request = new byte[4 + bytes.Length + 2];
            request[0] = 5;
            request[1] = 1;
            request[2] = 0;
            request[3] = isV6 ? (byte)4 : (byte)1;
            bytes.CopyTo(request, 4);
            request[^2] = (byte)(port >> 8);
            request[^1] = (byte)(port & 0xFF);
            return request;
        }
        else
        {
            var domain = Encoding.ASCII.GetBytes(host);
            if (domain.Length is < 1 or > 253) throw new ArgumentException("Неверное имя хоста.", nameof(host));
            var request = new byte[4 + 1 + domain.Length + 2];
            request[0] = 5;
            request[1] = 1;
            request[2] = 0;
            request[3] = 3;
            request[4] = (byte)domain.Length;
            domain.CopyTo(request, 5);
            request[^2] = (byte)(port >> 8);
            request[^1] = (byte)(port & 0xFF);
            return request;
        }
    }

    private static async Task PumpAsync(Stream client, Stream upstream)
    {
        var toUpstream = CopyUntilClosedAsync(client, upstream);
        var toClient = CopyUntilClosedAsync(upstream, client);
        await Task.WhenAny(toUpstream, toClient);
        // Полудуплекс не нужен: обрыв любой стороны гасит соединение.
        try { client.Close(); } catch { }
        try { upstream.Close(); } catch { }
    }

    private static async Task CopyUntilClosedAsync(Stream from, Stream to)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            int read;
            while ((read = await from.ReadAsync(buffer)) > 0)
                await to.WriteAsync(buffer.AsMemory(0, read));
        }
        catch { /* обрыв одной из сторон — нормален для прокси */ }
    }

    private static async Task<int> ReadAsync(Stream stream, byte[] buffer, int exact, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var total = 0;
        while (total < exact)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total, exact - total), cts.Token);
            if (read <= 0) break;
            total += read;
        }
        return total;
    }

    private static void AnnounceFallback(ChainRoute failed)
    {
        if (_fallbackAnnounced &&
            DateTimeOffset.UtcNow - _lastFallbackAnnounceUtc < TimeSpan.FromMinutes(10)) return;
        _fallbackAnnounced = true;
        _lastFallbackAnnounceUtc = DateTimeOffset.UtcNow;
        var tunnel = failed == ChainRoute.Tor ? "туннель анонимного слоя" : "транспорт сервера";
        Ui.Post(() =>
        {
            VoiceAssistantService.Announce(
                tunnel + " недоступен. Иду напрямую с максимальной защитой: IP реальный.",
                VoiceAnnouncementPriority.Important);
            CrashReportService.AddBreadcrumb("chain-router", "fallback-direct");
        });
    }
}
