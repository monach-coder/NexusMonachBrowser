using System.Net;
using System.Net.NetworkInformation;

namespace NexusMonach.Services;

/// <summary>
/// Фоновый сборщик сетевых метрик для статус-бара.
/// GetAllNetworkInterfaces под VPN-фильтрами (AdGuard, WireGuard) может занимать
/// сотни миллисекунд; вызов каждую секунду на UI-потоке замораживал окно.
/// Здесь опрос и пинг всегда в фоне, UI читает готовый снапшот.
/// </summary>
public static class NetworkPerformanceSampler
{
    public sealed record Snapshot(
        double DownloadBytesPerSecond,
        double UploadBytesPerSecond,
        long? PingMilliseconds);

    private static readonly object Gate = new();
    private static Task? _loop;
    private static Snapshot _last = new(0, 0, null);
    private static readonly Dictionary<string, (long Received, long Sent)> Counters = new(StringComparer.Ordinal);
    private static DateTime _sampleUtc = DateTime.UtcNow;
    private static string? _gateway;
    private static DateTime _gatewayUtc = DateTime.MinValue;
    private static int _tick;
    private static bool _pingBusy;

    public static Snapshot Current
    {
        get { lock (Gate) return _last; }
    }

    public static void EnsureStarted()
    {
        lock (Gate)
        {
            _loop ??= Task.Run(SampleLoopAsync);
        }
    }

    private static async Task SampleLoopAsync()
    {
        while (true)
        {
            double down = 0, up = 0;
            long? ping = null;
            try
            {
                var now = DateTime.UtcNow;
                var seconds = Math.Max(0.2, (now - _sampleUtc).TotalSeconds);
                _sampleUtc = now;
                var samples = new List<(double Down, double Up)>();
                string? seenGateway = null;
                foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (adapter.OperationalStatus != OperationalStatus.Up ||
                        adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                        continue;
                    var ip = adapter.GetIPProperties();
                    if (seenGateway is null)
                    {
                        var gw = ip.GatewayAddresses
                            .Select(x => x.Address)
                            .FirstOrDefault(x => !x.Equals(IPAddress.Any) && !x.Equals(IPAddress.IPv6Any));
                        if (gw is not null)
                        {
                            seenGateway = gw.ToString();
                            _gateway = seenGateway;
                            _gatewayUtc = now;
                        }
                    }
                    var stats = adapter.GetIPStatistics();
                    if (Counters.TryGetValue(adapter.Id, out var previous))
                        samples.Add((Math.Max(0, stats.BytesReceived - previous.Received) / seconds,
                                     Math.Max(0, stats.BytesSent - previous.Sent) / seconds));
                    Counters[adapter.Id] = (stats.BytesReceived, stats.BytesSent);
                }
                // Шлюз пропал (сменили сеть) — сбрасываем не сразу, а через минуту тишины.
                if (seenGateway is null && now - _gatewayUtc > TimeSpan.FromSeconds(60))
                    _gateway = null;
                if (samples.Count > 0)
                {
                    var active = samples.OrderByDescending(x => x.Down + x.Up).First();
                    down = active.Down;
                    up = active.Up;
                }

                if (++_tick % 5 == 0 && !_pingBusy && _gateway is not null)
                {
                    _pingBusy = true;
                    try
                    {
                        using var pinger = new Ping();
                        var reply = await pinger.SendPingAsync(_gateway, 1500);
                        ping = reply.Status == IPStatus.Success ? reply.RoundtripTime : null;
                    }
                    catch { /* Шлюз может не отвечать на ICMP — это не ошибка. */ }
                    finally { _pingBusy = false; }
                }
            }
            catch { /* Счётчики отдельных драйверов VPN могут быть недоступны. */ }

            long? carriedPing;
            lock (Gate) carriedPing = _last.PingMilliseconds;
            lock (Gate) _last = new Snapshot(down, up, ping ?? carriedPing);
            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}
