using NexusMonach.Services.Chain;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Встроенный маршрутизатор цепочки: выбор маршрута, построение SOCKS5-запросов
/// и живой цикл через loopback-цель (без внешней сети). Одна коллекция с
/// VlessChainTests: маршрутизатор статический и влияет на аргументы прокси.
/// </summary>
[Collection("chain-router")]
public class ChainRouterTests
{
    [Fact]
    public void PickRoute_TorOnlyWhenInChainAndWrapped()
    {
        Assert.Equal(ChainRoute.Tor, ChainRouterService.PickRoute(
            torInChain: true, torWrapped: true,
            vlessEnabled: true, vlessRunning: true,
            customProxyEnabled: true));

        // Слой в цепочке, но без обёртки — напрямую он не выходит.
        Assert.Equal(ChainRoute.Transport, ChainRouterService.PickRoute(
            torInChain: true, torWrapped: false,
            vlessEnabled: true, vlessRunning: true,
            customProxyEnabled: true));

        // Слой исключён из цепочки — транспорт не перекрывается.
        Assert.Equal(ChainRoute.Transport, ChainRouterService.PickRoute(
            torInChain: false, torWrapped: true,
            vlessEnabled: true, vlessRunning: true,
            customProxyEnabled: true));
    }

    [Fact]
    public void PickRoute_CustomProxyWhenNoTunnel()
    {
        Assert.Equal(ChainRoute.CustomProxy, ChainRouterService.PickRoute(
            torInChain: true, torWrapped: false,
            vlessEnabled: false, vlessRunning: false,
            customProxyEnabled: true));

        // Выключенный тумблер транспорта не должен занимать маршрут.
        Assert.Equal(ChainRoute.CustomProxy, ChainRouterService.PickRoute(
            torInChain: false, torWrapped: false,
            vlessEnabled: false, vlessRunning: true,
            customProxyEnabled: true));
    }

    [Fact]
    public void PickRoute_DirectIsTheBuiltInDefault()
    {
        Assert.Equal(ChainRoute.Direct, ChainRouterService.PickRoute(
            torInChain: true, torWrapped: false,
            vlessEnabled: false, vlessRunning: false,
            customProxyEnabled: false));

        Assert.Equal(ChainRoute.Direct, ChainRouterService.PickRoute(
            torInChain: false, torWrapped: false,
            vlessEnabled: false, vlessRunning: false,
            customProxyEnabled: false));
    }

    [Fact]
    public void BuildConnectRequest_DomainKeepsRemoteDns()
    {
        var request = ChainRouterService.BuildConnectRequest("example.org", 443);

        Assert.Equal(5, request[0]);
        Assert.Equal(1, request[1]);
        Assert.Equal(0, request[2]);
        Assert.Equal(3, request[3]); // ATYP: домен
        Assert.Equal(11, request[4]); // длина example.org
        Assert.Equal("example.org",
            System.Text.Encoding.ASCII.GetString(request, 5, 11));
        Assert.Equal(443, (request[^2] << 8) | request[^1]);
    }

    [Fact]
    public void BuildConnectRequest_Ipv4UsesAddressType()
    {
        var request = ChainRouterService.BuildConnectRequest("127.0.0.1", 9050);

        Assert.Equal(1, request[3]); // ATYP: IPv4
        Assert.Equal(4 + 4 + 2, request.Length);
        Assert.Equal(127, request[4]);
        Assert.Equal(0, request[5]);
        Assert.Equal(0, request[6]);
        Assert.Equal(1, request[7]);
        Assert.Equal(9050, (request[^2] << 8) | request[^1]);
    }

    [Fact]
    public void BuildConnectRequest_RejectsBadDomain()
    {
        Assert.Throws<ArgumentException>(() =>
            ChainRouterService.BuildConnectRequest("", 80));
    }

    // ── Живой цикл через loopback: без интернета и внешних сервисов ──

    [Fact]
    public async Task Router_ConnectsDirectlyToLoopbackTarget()
    {
        try
        {
            // Эхо-сервер на loopback — цель прямого маршрута.
            var echo = System.Net.Sockets.TcpListener.Create(0);
            echo.Start(4);
            var echoPort = ((System.Net.IPEndPoint)echo.LocalEndpoint).Port;
            var echoTask = Task.Run(async () =>
            {
                using var accepted = await echo.AcceptTcpClientAsync();
                var buffer = new byte[64];
                var stream = accepted.GetStream();
                var read = await stream.ReadAsync(buffer);
                await stream.WriteAsync(buffer.AsMemory(0, read));
            });

            ChainRouterService.Start();
            Assert.True(ChainRouterService.IsRunning, "маршрутизатор не поднялся");

            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, ChainRouterService.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            var greeting = new byte[2];
            await ReadExactAsync(stream, greeting);
            Assert.Equal(0, greeting[1]); // без аутентификации

            var request = ChainRouterService.BuildConnectRequest(
                System.Net.IPAddress.Loopback.ToString(), echoPort);
            await stream.WriteAsync(request);
            var reply = new byte[10];
            await ReadExactAsync(stream, reply);
            Assert.Equal(0, reply[1]); // REP = успех

            var probe = "привет, маршрут"u8.ToArray();
            await stream.WriteAsync(probe);
            var echoed = new byte[probe.Length];
            await ReadExactAsync(stream, echoed);
            Assert.Equal(probe, echoed);
            await echoTask.WaitAsync(TimeSpan.FromSeconds(10));
            echo.Stop();
        }
        finally
        {
            ChainRouterService.Stop();
        }
    }

    [Fact]
    public async Task Router_DeadTarget_GivesHonestSocksWithinSeconds()
    {
        try
        {
            // Закрытый порт на loopback: маршрутизатор обязан ЯВНО отказать
            // (REP=1), а не молча оборвать — тихий обрыв вешает вкладки
            // до собственных таймаутов движка.
            var deadPort = FindClosedLoopbackPort();
            ChainRouterService.Start();
            Assert.True(ChainRouterService.IsRunning, "маршрутизатор не поднялся");

            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, ChainRouterService.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            var greeting = new byte[2];
            await ReadExactAsync(stream, greeting);

            var request = ChainRouterService.BuildConnectRequest(
                System.Net.IPAddress.Loopback.ToString(), deadPort);
            await stream.WriteAsync(request);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            // Отказ приходит кадром SOCKS5 — читаем ровно заголовок ответа.
            var firstTwo = new byte[2];
            await ReadExactAsync(stream, firstTwo, timeout.Token);
            Assert.Equal(5, firstTwo[0]);
            Assert.Equal(1, firstTwo[1]); // REP = general SOCKS server failure
        }
        finally
        {
            ChainRouterService.Stop();
        }
    }

    [Fact]
    public async Task Router_DropAllTunnels_CutsLiveConnections()
    {
        try
        {
            // Живой туннель через роутер обязан обрываться при переброске
            // маршрута: иначе движок катается по старым keep-alive сокетам
            // и переключение тумблера «не работает» на глаз.
            var echo = System.Net.Sockets.TcpListener.Create(0);
            echo.Start(4);
            var echoPort = ((System.Net.IPEndPoint)echo.LocalEndpoint).Port;
            var echoTask = Task.Run(async () =>
            {
                using var accepted = await echo.AcceptTcpClientAsync();
                var buffer = new byte[256];
                var stream = accepted.GetStream();
                while (await stream.ReadAsync(buffer) > 0) { /* держим туннель */ }
            });

            ChainRouterService.Start();
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, ChainRouterService.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            var greeting = new byte[2];
            await ReadExactAsync(stream, greeting);
            await stream.WriteAsync(
                ChainRouterService.BuildConnectRequest("127.0.0.1", echoPort));
            var reply = new byte[10];
            await ReadExactAsync(stream, reply);
            Assert.Equal(0, reply[1]);

            ChainRouterService.DropAllTunnels();

            // Движок видит обрыв как EOF/RST — читаем до него с таймаутом.
            var probe = new byte[8];
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var sawClose = false;
            try
            {
                var read = await stream.ReadAsync(probe, deadline.Token);
                sawClose = read == 0; // EOF — туннель срезан
            }
            catch (System.Net.Sockets.SocketException) { sawClose = true; }
            catch (OperationCanceledException) { sawClose = false; }
            Assert.True(sawClose, "туннель не был разорван DropAllTunnels");
            await echoTask.WaitAsync(TimeSpan.FromSeconds(10));
            echo.Stop();
        }
        finally
        {
            ChainRouterService.Stop();
        }
    }

    [Fact]
    public void ParseDohIpv4_TakesOnlyARecords()
    {
        var json = """
        {"Status":0,"Answer":[
          {"name":"ya.ru","type":5,"TTL":300,"data":"ya.ru"},
          {"name":"ya.ru","type":1,"TTL":300,"data":"77.88.44.242"},
          {"name":"ya.ru","type":28,"TTL":300,"data":"2a02:6b8::2:242"},
          {"name":"ya.ru","type":1,"TTL":300,"data":"5.255.255.242"}]}
        """;
        Assert.Equal(new[] { "77.88.44.242", "5.255.255.242" }, ChainRouterService.ParseDohIpv4(json));
    }

    [Fact]
    public void ParseDohIpv4_BrokenJson_Empty()
    {
        Assert.Empty(ChainRouterService.ParseDohIpv4("{oops"));
        Assert.Empty(ChainRouterService.ParseDohIpv4("""{"Status":2}"""));
    }

    [Fact]
    public void DnsTcp_QueryBuildsAndParses()
    {
        // Запрос: заголовок 12 байт + имя + A/IN.
        var query = ChainRouterService.BuildDnsQuery("ya.ru");
        Assert.True(query.Length > 16);
        Assert.Equal(0x01, query[2]); // рекурсия
        Assert.Equal(0, query[4]);    // qdcount: старший байт
        Assert.Equal(1, query[5]);    // qdcount = 1

        // Ответ: вопрос ya.ru + CNAME + A-запись 77.88.44.242 (со сжатием имени).
        var response = new System.Collections.Generic.List<byte>();
        response.AddRange(new byte[] { 0x12, 0x34, 0x81, 0x80, 0, 1, 0, 2, 0, 0, 0, 0 });
        response.AddRange(new byte[] { 2, (byte)'y', (byte)'a', 2, (byte)'r', (byte)'u', 0, 0, 1, 0, 1 });
        // CNAME: имя указателем (0xC0 0x0C), type 5, длина данных с именем
        response.AddRange(new byte[] { 0xC0, 0x0C, 0, 5, 0, 1, 0, 0, 0, 60, 0, 6,
            3, (byte)'w', (byte)'w', (byte)'w', 0xC0, 0x0C });
        // A: указатель, type 1, TTL, длина 4, адрес
        response.AddRange(new byte[] { 0xC0, 0x0C, 0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 77, 88, 44, 242 });

        var addresses = ChainRouterService.ParseDnsA(response.ToArray());
        Assert.Single(addresses);
        Assert.Equal("77.88.44.242", addresses[0]);
    }

    [Fact]
    public async Task Router_ConnectsByDomainName_Loopback()
    {
        // Регрессия одного байта: для доменной записи head[4] — ДЛИНА имени,
        // и имя читается целиком со следующего байта. Кривой парсер собирал
        // имя с ведущим нулём и без последней буквы — и все домены умирали.
        try
        {
            var echo = System.Net.Sockets.TcpListener.Create(0);
            echo.Start(4);
            var echoPort = ((System.Net.IPEndPoint)echo.LocalEndpoint).Port;
            var echoTask = Task.Run(async () =>
            {
                using var accepted = await echo.AcceptTcpClientAsync();
                var buffer = new byte[64];
                var stream = accepted.GetStream();
                var read = await stream.ReadAsync(buffer);
                await stream.WriteAsync(buffer.AsMemory(0, read));
            });

            ChainRouterService.Start();
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(System.Net.IPAddress.Loopback, ChainRouterService.Port);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 });
            var greeting = new byte[2];
            await ReadExactAsync(stream, greeting);

            var request = ChainRouterService.BuildConnectRequest("localhost", echoPort);
            await stream.WriteAsync(request);
            var reply = new byte[10];
            await ReadExactAsync(stream, reply);
            Assert.Equal(0, reply[1]);

            var probe = "домен-жив"u8.ToArray();
            await stream.WriteAsync(probe);
            var echoed = new byte[probe.Length];
            await ReadExactAsync(stream, echoed);
            Assert.Equal(probe, echoed);
            await echoTask.WaitAsync(TimeSpan.FromSeconds(10));
            echo.Stop();
        }
        finally
        {
            ChainRouterService.Stop();
        }
    }

    private static int FindClosedLoopbackPort()
    {
        for (var port = 49000; port < 49500; port++)
        {
            try
            {
                var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
                probe.Start(1);
                probe.Stop();
                return port;
            }
            catch (System.Net.Sockets.SocketException) { /* занят — следующий */ }
        }
        throw new InvalidOperationException("Не нашли свободный порт для теста.");
    }

    private static async Task ReadExactAsync(
        System.IO.Stream stream, byte[] buffer, System.Threading.CancellationToken token = default)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), token);
            if (read <= 0) throw new System.IO.EndOfStreamException("Соединение оборвано раньше ответа.");
            total += read;
        }
    }
}
