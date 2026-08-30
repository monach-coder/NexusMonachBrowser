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
    /// Живое состояние обёртки Тора: процесс крутится И есть туннель
    /// (транспорт или системный VPN). Слой без обёртки не выходит в сеть —
    /// через него вкладки не пускаются.
    /// </summary>
    internal static bool IsTorWrapped() =>
        Services.Tor.TorService.IsRunning &&
        (Services.Vless.VlessRuntime.IsRunning ||
         Services.Tor.VpnDetector.DetectCached().VpnActive);

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
                var pending = addressBytes.Length - 1;
                if (pending > 0)
                {
                    var rest = new byte[pending];
                    if (await ReadAsync(stream, rest, pending, TimeSpan.FromSeconds(10)) < pending)
                        return;
                    Array.Copy(rest, 0, addressBytes, 1, pending);
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
                var addresses = await ResolveHostAsync(host);
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
    /// Лестница разрешения имён для прямого маршрута. Первый этаж — системный
    /// резолвер с окном 2 секунды: на здоровой машине ответ мгновенный.
    /// Второй этаж — DNS-over-HTTPS к 1.1.1.1 по IP-литералу: спасает, когда
    /// локальный DNS перехвачен, отфильтрован по приложениям или душит AAAA.
    /// </summary>
    private static async Task<IPAddress[]> ResolveHostAsync(string host)
    {
        if (IPAddress.TryParse(host, out var literal)) return [literal];
        try
        {
            using var window = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            var all = await Dns.GetHostAddressesAsync(host).WaitAsync(window.Token);
            var ipv4 = all.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToArray();
            if (ipv4.Length > 0) return ipv4;
            if (all.Length > 0) return all;
        }
        catch { /* системный резолвер молчит — следующий этаж */ }
        var viaDoh = await ResolveViaDohAsync(host);
        if (viaDoh.Length > 0)
        {
            CrashReportService.AddBreadcrumb("chain-router", "dns-via-doh");
            return viaDoh;
        }
        throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>DoH-клиент без системного прокси: адрес — IP-литерал.</summary>
    private static readonly HttpClient DohClient = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(3)
    };

    private static async Task<IPAddress[]> ResolveViaDohAsync(string host)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://1.1.1.1/dns-query?name=" + Uri.EscapeDataString(host) + "&type=A");
            request.Headers.Accept.ParseAdd("application/dns-json");
            using var response = await DohClient.SendAsync(request);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            return ParseDohIpv4(json).Select(IPAddress.Parse).ToArray();
        }
        catch
        {
            return [];
        }
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
