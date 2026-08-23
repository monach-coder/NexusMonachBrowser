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
    private static bool IsVpnAdapter(NetworkInterface ni)
    {
        if (ni.NetworkInterfaceType is NetworkInterfaceType.Ppp
            or NetworkInterfaceType.Tunnel)
            return true;

        var description = ni.Description.ToLowerInvariant();
        var name = ni.Name.ToLowerInvariant();

        return description.Contains("wireguard") ||
               description.Contains("openvpn") ||
               description.Contains("tap-windows") ||
               description.Contains("tap adapter") ||
               description.Contains("vpn") ||
               description.Contains("tunnel") ||
               name.Contains("wireguard") ||
               name.Contains("openvpn") ||
               name.Contains("vpn") ||
               name.Contains("tunnel");
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
