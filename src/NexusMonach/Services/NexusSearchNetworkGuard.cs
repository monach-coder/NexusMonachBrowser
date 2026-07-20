using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace NexusMonach.Services;

/// <summary>
/// Network boundary for the bounded crawler. Direct connections are resolved,
/// validated and connected to the exact public address so a second DNS answer
/// cannot redirect the request into the local network.
/// </summary>
internal static class NexusSearchNetworkGuard
{
    private static readonly string[] LocalHostSuffixes =
    [
        ".localhost", ".local", ".internal", ".lan", ".home.arpa", ".localdomain"
    ];

    public static bool TryParsePublicHttpUri(string value, out Uri uri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out uri!) ||
            uri.Scheme is not "http" and not "https" ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !uri.IsDefaultPort)
            return false;

        var host = NormalizeHost(uri);
        if (string.IsNullOrWhiteSpace(host) || host.Length > 253 ||
            host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
            LocalHostSuffixes.Any(suffix => host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
            return IsPublicAddress(literal);

        // Single-label names are normally intranet or resolver search-suffix
        // targets. The crawler has no reason to contact them.
        return host.Contains('.');
    }

    public static async Task ValidatePublicDestinationAsync(string value, CancellationToken cancellationToken)
    {
        if (!TryParsePublicHttpUri(value, out var uri))
            throw new HttpRequestException("Crawl Engine заблокировал небезопасный сетевой адрес.");

        _ = await ResolvePublicAddressesAsync(uri, cancellationToken).ConfigureAwait(false);
    }

    public static async ValueTask<Stream> ConnectPublicAsync(SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        if (endpoint.Port is not 80 and not 443)
            throw new HttpRequestException("Crawl Engine разрешает только стандартные HTTP/HTTPS-порты.");

        var scheme = endpoint.Port == 443 ? "https" : "http";
        var uri = new UriBuilder(scheme, endpoint.Host, endpoint.Port).Uri;
        var addresses = await ResolvePublicAddressesAsync(uri, cancellationToken).ConfigureAwait(false);
        Exception? lastError = null;

        foreach (var address in addresses.OrderBy(x => x.AddressFamily == AddressFamily.InterNetwork ? 0 : 1))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        throw new HttpRequestException("Не удалось установить защищённое соединение с публичным адресом.", lastError);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        var bytes = address.GetAddressBytes();

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return !(IPAddress.IsLoopback(address) ||
                     bytes[0] is 0 or 10 or 127 ||
                     bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                     bytes[0] == 169 && bytes[1] == 254 ||
                     bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                     bytes[0] == 192 && bytes[1] == 0 && bytes[2] is 0 or 2 ||
                     bytes[0] == 192 && bytes[1] == 168 ||
                     bytes[0] == 198 && bytes[1] is 18 or 19 ||
                     bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
                     bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113 ||
                     bytes[0] == 168 && bytes[1] == 63 && bytes[2] == 129 && bytes[3] == 16 ||
                     bytes[0] >= 224);
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6 ||
            address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.IPv6None) ||
            IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast || (bytes[0] & 0xfe) == 0xfc)
            return false;

        // Documentation, Teredo and NAT64 prefixes are not valid crawler
        // destinations. Blocking the transition ranges also prevents embedded
        // private IPv4 addresses from bypassing the IPv4 rules.
        if (HasPrefix(bytes, [0x20, 0x01, 0x0d, 0xb8], 32) ||
            HasPrefix(bytes, [0x20, 0x01, 0x00, 0x00], 32) ||
            HasPrefix(bytes, [0x00, 0x64, 0xff, 0x9b], 32))
            return false;

        if (bytes[0] == 0x20 && bytes[1] == 0x02)
            return IsPublicAddress(new IPAddress(bytes[2..6]));

        return true;
    }

    private static async Task<IReadOnlyList<IPAddress>> ResolvePublicAddressesAsync(Uri uri,
        CancellationToken cancellationToken)
    {
        var host = NormalizeHost(uri);
        IPAddress[] addresses;
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }

        var distinct = addresses.Distinct().Take(17).ToArray();
        if (distinct.Length == 0 || distinct.Length > 16 || distinct.Any(address => !IsPublicAddress(address)))
            throw new HttpRequestException("Crawl Engine заблокировал локальный, служебный или неоднозначный DNS-адрес.");

        return distinct;
    }

    private static string NormalizeHost(Uri uri) => uri.DnsSafeHost.TrimEnd('.');

    private static bool HasPrefix(byte[] value, byte[] prefix, int bits)
    {
        var bytes = bits / 8;
        for (var index = 0; index < bytes; index++)
            if (value[index] != prefix[index]) return false;
        return true;
    }
}
