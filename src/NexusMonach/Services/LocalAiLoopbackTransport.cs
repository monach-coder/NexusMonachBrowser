using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace NexusMonach.Services;

/// <summary>
/// HTTP transport for local AI runtimes. It never uses a proxy, follows no
/// redirects and can only open a socket to a loopback IP literal.
/// </summary>
internal static class LocalAiLoopbackTransport
{
    public static HttpClient CreateClient(bool automaticDecompression = false)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = automaticDecompression
                ? DecompressionMethods.All
                : DecompressionMethods.None,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectCallback = ConnectAsync
        };

        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public static void EnsureAllowedEndpoint(Uri endpoint)
    {
        if (!endpoint.IsAbsoluteUri || endpoint.Scheme != Uri.UriSchemeHttp ||
            !string.IsNullOrEmpty(endpoint.UserInfo) || endpoint.Port is < 1024 or > 65535 ||
            !TryGetLoopbackAddress(endpoint.Host, out _))
            throw new InvalidOperationException("Локальный AI-запрос заблокирован: недопустимый endpoint.");
    }

    private static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        CancellationToken cancellationToken)
    {
        var endpoint = context.DnsEndPoint;
        if (endpoint.Port is < 1024 or > 65535 ||
            !TryGetLoopbackAddress(endpoint.Host, out var address))
            throw new HttpRequestException("Локальный AI-транспорт заблокировал соединение вне loopback.");

        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        {
            NoDelay = true
        };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, endpoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static bool TryGetLoopbackAddress(string host, out IPAddress address)
    {
        address = IPAddress.None;
        host = host.Trim('[', ']');
        if (!IPAddress.TryParse(host, out var parsed) || !IPAddress.IsLoopback(parsed)) return false;
        address = parsed;
        return true;
    }
}
