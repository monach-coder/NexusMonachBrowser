using System.Net.NetworkInformation;

namespace NexusMonach.Services.Tor;

/// <summary>
/// Результат сканирования VPN на машине.
/// </summary>
public sealed record VpnDetectionResult(
    bool VpnActive,
    string AdapterName,
    string AdapterType,
    string? Gateway,
    List<int> OpenPorts);

/// <summary>
/// VPN-детектор: находит активные VPN-подключения (WireGuard, OpenVPN,
/// L2TP, SSTP) через сетевые интерфейсы Windows. Если VPN найден,
/// Tor может маршрутизироваться через него — цензор видит только VPN.
/// </summary>
public static class VpnDetector
{
    private static VpnDetectionResult? _cached;
    private static DateTime _cachedAt;
    private static readonly object Gate = new();

    /// <summary>
    /// Детект с кэшем на 30 секунд: полный скан (включая порты) слишком
    /// тяжёл, чтобы гонять его на каждый пересбор аргументов браузера.
    /// </summary>
    public static VpnDetectionResult DetectCached()
    {
        lock (Gate)
        {
            if (_cached is not null && DateTime.UtcNow - _cachedAt < TimeSpan.FromSeconds(30))
                return _cached;
        }
        var fresh = Detect();
        lock (Gate)
        {
            _cached = fresh;
            _cachedAt = DateTime.UtcNow;
        }
        return fresh;
    }

    /// <summary>
    /// Сканирует сетевые интерфейсы на активные VPN-подключения.
    /// Работает без прав администратора (только чтение).
    /// </summary>
    public static VpnDetectionResult Detect()
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
            .ToList();

        // VPN-адаптеры определяются по типу, описанию или имени.
        var vpnAdapter = adapters.FirstOrDefault(IsVpnAdapter);
        if (vpnAdapter is null)
            return new VpnDetectionResult(false, string.Empty, string.Empty, null, []);

        var gateway = GetGateway(vpnAdapter);
        var openPorts = ScanLocalPorts();

        return new VpnDetectionResult(
            true,
            vpnAdapter.Name,
            vpnAdapter.NetworkInterfaceType.ToString(),
            gateway,
            openPorts);
    }

    /// <summary>
    /// Определяет, является ли сетевой адаптер VPN-туннелем.
    /// </summary>
    private static bool IsVpnAdapter(NetworkInterface ni) =>
        IsVpnAdapter(ni.NetworkInterfaceType.ToString(), ni.Description, ni.Name);

    /// <summary>
    /// Чистая логика распознавания VPN-адаптера (проверяется юнит-тестами).
    /// Системные переходные псевдоинтерфейсы Windows — Teredo, ISATAP, 6to4 —
    /// всегда «Up» и имеют тип Tunnel; VPN они не являются. Без фильтра
    /// детектор вечно видел бы «VPN активен» и заворачивал вкладки в
    /// анонимный слой, который без настоящей обёртки не выходит в сеть.
    /// </summary>
    internal static bool IsVpnAdapter(string interfaceType, string description, string name)
    {
        var desc = description.ToLowerInvariant();
        var title = name.ToLowerInvariant();

        if (desc.Contains("teredo") || desc.Contains("isatap") || desc.Contains("6to4") ||
            title.Contains("teredo") || title.Contains("isatap") || title.Contains("6to4"))
            return false;

        if (desc.Contains("pseudo-interface") || title.Contains("pseudo-interface"))
            return false;

        if (interfaceType == NetworkInterfaceType.Ppp.ToString() ||
            interfaceType == NetworkInterfaceType.Tunnel.ToString())
            return true;

        return desc.Contains("wireguard") ||
               desc.Contains("openvpn") ||
               desc.Contains("tap-windows") ||
               desc.Contains("tap adapter") ||
               desc.Contains("vpn") ||
               desc.Contains("tunnel") ||
               desc.Contains("warp") ||
               desc.Contains("cloudflare") ||
               title.Contains("wireguard") ||
               title.Contains("openvpn") ||
               title.Contains("vpn") ||
               title.Contains("tunnel") ||
               title.Contains("warp") ||
               title.Contains("cloudflare");
    }

    /// <summary>
    /// Получает шлюз VPN-адаптера (если доступен).
    /// </summary>
    private static string? GetGateway(NetworkInterface ni)
    {
        try
        {
            var properties = ni.GetIPProperties();
            var gateway = properties.GatewayAddresses.FirstOrDefault();
            return gateway?.Address.ToString();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Сканирует типичные «утекающие» порты на localhost. Возвращает
    /// список открытых портов, которые могут деанонимизировать.
    /// </summary>
    private static List<int> ScanLocalPorts()
    {
        // Порты, через которые возможна утечка реального IP или DNS.
        var riskyPorts = new[]
        {
            53,     // DNS — может обходить Tor
            5353,   // mDNS — утечка локальной сети
            1900,   // SSDP/UPnP — утечка сетевой топологии
            3389,   // RDP — если открыт наружу
            5900,   // VNC — если открыт наружу
            1080,   // SOCKS-прокси — конфликт с Tor
            8118,   // Privoxy — может обходить Tor
        };

        var open = new List<int>();
        foreach (var port in riskyPorts)
        {
            if (TorService.Probe(port))
                open.Add(port);
        }
        return open;
    }
}
