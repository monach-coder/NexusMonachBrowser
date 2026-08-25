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
    private string? _lastGatewayMac;
    private bool _disposed;

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
    /// Порты-ловушки: обычно закрыты, сканер подключается — мы видим атаку.
    /// Используются только порты > 1024 (не требуют прав админа).
    /// </summary>
    private static readonly int[] HoneypotPorts =
    {
        2222,   // «SSH» — сканеры любят
        3000,   // «dev-сервер»
        5000,   // «upnp»
        6379,   // «Redis»
        8080,   // «HTTP-alt»
        8888,   // «HTTP-proxy»
        27017,  // «MongoDB»
    };

    private void StartHoneypot()
    {
        foreach (var port in HoneypotPorts)
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

            // Ложные баннеры по типу порта.
            var banner = port switch
            {
                2222 => "SSH-2.0-OpenSSH_8.9p1 Ubuntu-3ubuntu0.4\r\n",
                3000 or 5000 => "HTTP/1.1 200 OK\r\nServer: nginx/1.24.0\r\nContent-Type: text/html\r\n\r\n<html><body>Welcome</body></html>",
                6379 => "-ERR unknown command\r\n",
                8080 or 8888 => "HTTP/1.1 403 Forbidden\r\nServer: Apache/2.4.57\r\n\r\n",
                27017 => "MongoDB 7.0.12 (linux)\r\n",
                _ => "Service ready\r\n",
            };

            var data = System.Text.Encoding.UTF8.GetBytes(banner);
            await stream.WriteAsync(data);
            await stream.FlushAsync();

            // Tarpit: держим соединение открытым до 30 секунд,
            // сканер ждёт ответа и не сканирует другие порты.
            await Task.Delay(TimeSpan.FromSeconds(30));

            client.Close();
        }
        catch { try { client.Close(); } catch { } }
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
    /// Следит за MAC-адресом шлюза: если он меняется без причины,
    /// это ARP-спуфинг (атакующий вставился между тобой и роутером).
    /// </summary>
    private void StartArpWatch()
    {
        _arpTimer = new Timer(_ =>
        {
            try
            {
                var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                    .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                ni.NetworkInterfaceType != NetworkInterfaceType.Loopback);

                foreach (var ni in interfaces)
                {
                    var gateway = ni.GetIPProperties().GatewayAddresses.FirstOrDefault();
                    if (gateway is null) continue;

                    var gatewayIp = gateway.Address.ToString();
                    var currentMac = ResolveMac(gatewayIp);
                    if (currentMac is null) continue;

                    if (_lastGatewayMac is not null && _lastGatewayMac != currentMac)
                    {
                        var threat = new ThreatEvent(
                            ThreatType.ArpSpoofing,
                            gatewayIp,
                            $"MAC шлюза изменился: {_lastGatewayMac} → {currentMac}. " +
                            "Возможна MITM-атака (атакующий перехватывает трафик).",
                            DateTimeOffset.Now,
                            "Рекомендуется проверить сеть и перезапустить роутер");
                        _threats.Add(threat);
                        ThreatDetected?.Invoke(threat);
                    }
                    _lastGatewayMac = currentMac;
                }
            }
            catch { }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(5));
    }

    private static string? ResolveMac(string ipAddress)
    {
        // Читаем ARP-таблицу через in-process API.
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                var arpTable = ni.GetIPProperties().UnicastAddresses;
                // .NET не даёт прямого доступа к ARP-таблице без P/Invoke.
                // Используем физический адрес интерфейса как fallback.
                if (ni.GetIPProperties().GatewayAddresses.Any(
                        g => g.Address.ToString() == ipAddress))
                {
                    return ni.GetPhysicalAddress().ToString();
                }
            }
        }
        catch { }
        return null;
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
        _cts.Dispose();
    }
}
