using System.Net;
using System.Net.NetworkInformation;
using System.Security.Cryptography;
using NexusMonach.Models;

namespace NexusMonach.Services;

public sealed record NetworkIdentitySnapshot(
    string Id,
    string Mode,
    string Route,
    string Isolation,
    string WebRtc,
    bool RouteProtected,
    string Details);

/// <summary>
/// Describes the local browsing identity without making an external probe.
/// The identifiers are created once per browser process, are never persisted
/// and are never injected into web content or request headers.
/// </summary>
public static class NetworkIdentityService
{
    private static readonly string MainId = CreateId("MAIN");
    private static readonly string VeilId = CreateId("VEIL");

    public static NetworkIdentitySnapshot Capture(bool isPrivate)
    {
        var settings = SettingsService.Current;
        var vpn = HasVpnInterface();
        var identity = isPrivate ? VeilId : MainId;
        var mode = isPrivate ? "одноразовая" : "основная";
        var isolation = isPrivate ? "InPrivate · данные не переносятся" : "обычное локальное хранилище";
        var webRtc = settings.PreventWebRtcIpLeak ? "WebRTC защищён" : "WebRTC может раскрыть маршрут";

        var route = "DIRECT · системный маршрут";
        var protectedRoute = false;
        if (settings.EnableCustomProxy)
        {
            var tor = IsTorConfiguration(settings);
            var available = !tor || IsLoopbackPortListening(settings.ProxyPort);
            route = tor
                ? available
                    ? $"TOR SOCKS5 · порт {settings.ProxyPort} активен"
                    : $"TOR SOCKS5 · порт {settings.ProxyPort} недоступен"
                : $"{settings.ProxyKind.ToString().ToUpperInvariant()} proxy · {settings.ProxyHost}:{settings.ProxyPort}";
            protectedRoute = available;
        }
        else if (vpn)
        {
            route = "VPN-интерфейс · маршрут Windows";
            protectedRoute = true;
        }

        var details =
            $"Сетевая личность {identity} создаётся локально на время процесса и не передаётся сайтам. " +
            $"Режим: {mode}. Изоляция: {isolation}. Маршрут: {route}. {webRtc}. " +
            "Статус VPN означает наличие интерфейса; внешний адрес без сетевого запроса не проверяется.";

        return new NetworkIdentitySnapshot(identity, mode, route, isolation, webRtc, protectedRoute, details);
    }

    private static string CreateId(string prefix) =>
        prefix + "-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4));

    private static bool IsTorConfiguration(BrowserSettings settings) =>
        settings.ProxyKind == ProxyKind.Socks5 &&
        settings.ProxyPort is 9050 or 9150 &&
        IPAddress.TryParse(settings.ProxyHost.Trim().Trim('[', ']'), out var address) &&
        IPAddress.IsLoopback(address);

    private static bool IsLoopbackPortListening(int port)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners()
                .Any(endpoint => IPAddress.IsLoopback(endpoint.Address) && endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasVpnInterface()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(adapter =>
            {
                if (adapter.OperationalStatus != OperationalStatus.Up ||
                    adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                    return false;

                if (adapter.NetworkInterfaceType is NetworkInterfaceType.Tunnel or NetworkInterfaceType.Ppp)
                    return true;

                var descriptor = (adapter.Name + " " + adapter.Description).ToLowerInvariant();
                return new[] { "vpn", "wireguard", "wintun", "openvpn", "tap", "tailscale", "zerotier" }
                    .Any(descriptor.Contains);
            });
        }
        catch
        {
            return false;
        }
    }
}
