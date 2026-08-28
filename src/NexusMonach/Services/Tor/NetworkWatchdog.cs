using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.NetworkInformation;

namespace NexusMonach.Services.Tor;

/// <summary>
/// Тип обнаруженной угрозы.
/// </summary>
public enum ThreatType
{
    /// <summary>Сканирование портов.</summary>
    PortScan,
    /// <summary>ARP-спуфинг (MITM).</summary>
    ArpSpoofing,
    /// <summary>DNS-запрос мимо Tor.</summary>
    DnsLeak,
    /// <summary>Подключение к подозрительному хосту.</summary>
    SuspiciousConnection,
    /// <summary>Подключение к honeypot-порту.</summary>
    HoneypotTriggered,
}

/// <summary>
/// Событие угрозы для UI.
/// </summary>
public sealed record ThreatEvent(
    ThreatType Type,
    string Source,
    string Details,
    DateTimeOffset DetectedAt,
    string Countermeasure);

/// <summary>
/// Сетевой Дозор: активный мониторинг и противодействие.
/// Honeypot привлекает сканеров, деceiver их обманывает, страж
/// обнаруживает и сбрасывает атаки — всё in-process, без админки.
/// </summary>
public sealed class NetworkWatchdog : IDisposable
{
    private readonly ConcurrentBag<ThreatEvent> _threats = [];
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedSources = new();
    private readonly List<TcpListener> _honeypotListeners = [];
    private readonly CancellationTokenSource _cts = new();
    private Timer? _monitorTimer;
    private Timer? _arpTimer;
    private bool _disposed;

    public NetworkWatchdog()
    {
        // Порты-ловушки выбираются на сессию: сканер, изучивший открытый
        // код, знает пул, но не знает, какие семь выбраны сейчас.
        _sessionPorts = SelectHoneypotPorts();
    }

    /// <summary>Новые угрозы — UI подписывается для живых уведомлений.</summary>
    public event Action<ThreatEvent>? ThreatDetected;

    /// <summary>Все обнаруженные угрозы за сессию.</summary>
    public IReadOnlyList<ThreatEvent> Threats => _threats.ToList();

    /// <summary>Заблокированные источники (IP → время блокировки).</summary>
    public IReadOnlyDictionary<string, DateTimeOffset> BlockedSources =>
        _blockedSources.ToDictionary(k => k.Key, v => v.Value);

    /// <summary>
    /// Запускает все компоненты Дозора: honeypot, монитор соединений,
    /// ARP-страж, DNS-страж.
    /// </summary>
    public void Start()
    {
        if (_disposed) return;
        StartHoneypot();
        StartConnectionMonitor();
        StartArpWatch();
    }

    // ═══════════════════════════════════════════
    // HONEYPOT — ловушка для сканеров
    // ═══════════════════════════════════════════

    /// <summary>
    /// Порты-ловушки: на каждую сессию случайным образом выбирается
    /// подмножество пула, поэтому знание механизма (открытый код) не даёт
    /// сканеру бесплатного фильтра — в его сессии набор другой.
    /// Только порты > 1024, без административных служб Windows.
    /// </summary>
    internal static readonly int[] HoneypotPool =
    {
        1080,   // «SOCKS-прокси»
        2222,   // «SSH»
        3000,   // «dev-сервер»
        3128,   // «squid»
        4444,   // «metasploit»
        5000,   // «upnp/dev»
        5555,   // «adb/dev»
        5672,   // «AMQP»
        6379,   // «Redis»
        8000,   // «HTTP-alt»
        8008,   // «HTTP-alt»
        8080,   // «HTTP-alt»
        8081,   // «HTTP-alt»
        8443,   // «HTTPS-alt»
        8888,   // «HTTP-proxy»
        9000,   // «SonarQube/HTTP»
        9090,   // «Prometheus»
        9200,   // «Elasticsearch»
        11211,  // «Memcached»
        15672,  // «RabbitMQ UI»
        27017,  // «MongoDB»
        50000,  // «upnp/SAP»
    };

    /// <summary>Порты ловушек текущей сессии (для сканера портов).
    /// internal-сеттер — шов для юнит-тестов классификации портов.</summary>
    public static IReadOnlyList<int>? ActiveSessionPorts { get; internal set; }

    private readonly int[] _sessionPorts;

    /// <summary>
    /// Выбирает случайные порты-ловушки из пула (крипто-ШБФ): порядок
    /// пула не имеет значения, выбор равномерный.
    /// </summary>
    internal static int[] SelectHoneypotPorts(int count = 7)
    {
        var pool = (int[])HoneypotPool.Clone();
        for (var i = pool.Length - 1; i > 0; i--)
        {
            var j = System.Security.Cryptography.RandomNumberGenerator.GetInt32(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }
        return pool.Take(count).ToArray();
    }

    private void StartHoneypot()
    {
        foreach (var port in _sessionPorts)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Any, port);
                listener.Start(4);
                _honeypotListeners.Add(listener);
                _ = AcceptHoneypotAsync(listener, port, _cts.Token);
            }
            catch (SocketException)
            {
                // Порт занят — сканер увидит живой сервис, тоже полезно.
            }
        }
        ActiveSessionPorts = _sessionPorts;
    }

    /// <summary>
    /// Принимает подключение к honeypot-порту: фиксируем атаку,
    /// отправляем обманный баннер и сбрасываем соединение.
    /// </summary>
    private async Task AcceptHoneypotAsync(TcpListener listener, int port,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(ct);
                var remoteIp = (client.Client.RemoteEndPoint as IPEndPoint)?
                    .Address.ToString() ?? "неизвестный";

                var threat = new ThreatEvent(
                    ThreatType.HoneypotTriggered,
                    remoteIp,
                    $"Подключение к ловушке на порту {port}",
                    DateTimeOffset.Now,
                    "Источник заблокирован, отправлен ложный баннер");

                _threats.Add(threat);
                _blockedSources.TryAdd(remoteIp, DateTimeOffset.Now);
                ThreatDetected?.Invoke(threat);

                // Обман сканера: отправляем ложный баннер сервиса.
                _ = DeceiveScanner(client, port);
            }
            catch (OperationCanceledException) { break; }
            catch { }
        }
    }

    // ═══════════════════════════════════════════
    // DECEIVER — обман сканера
    // ═══════════════════════════════════════════

    /// <summary>
    /// Отправляет сканеру ложный баннер, чтобы он думал что нашёл
    /// настоящий сервис. Затем держит соединение (tarpit) — сканер
    /// тратит время и не сканирует дальше.
    /// </summary>
    private async Task DeceiveScanner(TcpClient client, int port)
    {
        try
        {
            client.ReceiveTimeout = 5000;
            client.SendTimeout = 5000;
            var stream = client.GetStream();

            var data = System.Text.Encoding.UTF8.GetBytes(PickBanner(port));
            await stream.WriteAsync(data);
            await stream.FlushAsync();

            // Tarpit: держим соединение случайное время (20–60 с) —
            // сканер ждёт ответа и не сканирует другие порты, а
            // предсказать задержку по документации нельзя.
            await Task.Delay(TimeSpan.FromSeconds(PickTarpitSeconds()));

            client.Close();
        }
        catch { try { client.Close(); } catch { } }
    }

    /// <summary>Случайная длительность тарпита, секунды.</summary>
    internal static int PickTarpitSeconds() =>
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(20, 61);

    /// <summary>
    /// Ложный баннер по семейству порта с рандомизированной версией —
    /// сигнатура ловушки не повторяется между сессиями и подключениями.
    /// </summary>
    internal static string PickBanner(int port)
    {
        static string Pick(params string[] versions) =>
            versions[System.Security.Cryptography.RandomNumberGenerator.GetInt32(versions.Length)];

        return port switch
        {
            2222 => $"SSH-2.0-OpenSSH_{Pick("8.9p1", "9.3p1", "9.6p1")} " +
                    Pick("Ubuntu-3ubuntu0.4", "Debian-3+deb12u2", "") + "\r\n",
            6379 => "-ERR unknown command\r\n",
            27017 => $"MongoDB {Pick("7.0.12", "6.0.18", "8.0.4")} (linux)\r\n",
            9200 => "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n" +
                    $"{{\"cluster_name\":\"elasticsearch\",\"version\":{{\"number\":\"{Pick("8.13.4", "7.17.18")}\"}}}}",
            5672 => "AMQP\x00\x00\x09\x01",
            3000 or 5000 or 5555 or 8000 or 8008 or 9000 or 9090 or 15672 =>
                "HTTP/1.1 200 OK\r\n" +
                $"Server: nginx/{Pick("1.24.0", "1.26.2", "1.22.1")}\r\n" +
                "Content-Type: text/html\r\n\r\n<html><body>Welcome</body></html>",
            1080 or 3128 or 4444 or 8080 or 8081 or 8443 or 8888 or 50000 =>
                "HTTP/1.1 403 Forbidden\r\n" +
                $"Server: Apache/{Pick("2.4.57", "2.4.62", "2.4.52")}\r\n\r\n",
            _ => "Service ready\r\n",
        };
    }

    // ═══════════════════════════════════════════
    // MONITOR — монитор соединений + DNS-страж
    // ═══════════════════════════════════════════

    /// <summary>
    /// Мониторит все TCP-соединения каждые 3 секунды (in-process,
    /// без netstat). Ищет DNS-утечки и подозрительные подключения.
    /// </summary>
    private void StartConnectionMonitor()
    {
        _monitorTimer = new Timer(_ =>
        {
            try
            {
                var connections = IPGlobalProperties.GetIPGlobalProperties()
                    .GetActiveTcpConnections();

                foreach (var conn in connections)
                {
                    // DNS-страж: любой процесс ходит на порт 53 напрямую
                    // (мимо Tor SOCKS) — это утечка.
                    if (conn.RemoteEndPoint.Port == 53 &&
                        !conn.RemoteEndPoint.Address.ToString().StartsWith("127.0.0.1"))
                    {
                        var source = conn.RemoteEndPoint.Address.ToString();
                        if (!_blockedSources.ContainsKey("dns:" + source))
                        {
                            _blockedSources.TryAdd("dns:" + source, DateTimeOffset.Now);
                            var threat = new ThreatEvent(
                                ThreatType.DnsLeak,
                                source,
                                $"Процесс {GetProcessName(conn)} отправил DNS на {source}:{conn.RemoteEndPoint.Port} (мимо Tor)",
                                DateTimeOffset.Now,
                                "DNS-запрос заблокирован через PortGuard");
                            _threats.Add(threat);
                            ThreatDetected?.Invoke(threat);
                        }
                    }
                }
            }
            catch { }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(3));
    }

    private static string GetProcessName(TcpConnectionInformation conn)
    {
        try
        {
            // TcpConnectionInformation не даёт PID в .NET 8,
            // но даёт локальный/удалённый адрес — этого достаточно.
            return $"local:{conn.LocalEndPoint}";
        }
        catch { return "неизвестный"; }
    }

    // ═══════════════════════════════════════════
    // ARP-СТРАЖ — обнаружение MITM
    // ═══════════════════════════════════════════

    /// <summary>
    /// Следит за MAC-адресом шлюза через настоящую ARP-таблицу ядра
    /// (GetIpNetTable). Смена MAC у одного и того же интерфейса —
    /// ARP-спуфинг: страж собирает доказательства, закрепляет шлюз
    /// статической записью и сообщает один раз на факт.
    /// </summary>
    private void StartArpWatch()
    {
        _arpTimer = new Timer(_ =>
        {
            try
            {
                var threat = ArpGuard.Check();
                if (threat is null) return;
                _threats.Add(threat);
                ThreatDetected?.Invoke(threat);
            }
            catch { }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    // ═══════════════════════════════════════════
    // УПРАВЛЕНИЕ
    // ═══════════════════════════════════════════

    /// <summary>
    /// Проверяет, заблокирован ли IP-адрес (сканер или DNS-утечка).
    /// </summary>
    public bool IsBlocked(string source) => _blockedSources.ContainsKey(source);

    /// <summary>Снимает блокировку с источника.</summary>
    public void Unblock(string source) => _blockedSources.TryRemove(source, out _);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _monitorTimer?.Dispose();
        _arpTimer?.Dispose();
        foreach (var listener in _honeypotListeners)
        {
            try { listener.Stop(); } catch { }
        }
        if (ActiveSessionPorts is not null && ActiveSessionPorts.SequenceEqual(_sessionPorts))
            ActiveSessionPorts = null;
        _cts.Dispose();
    }
}
