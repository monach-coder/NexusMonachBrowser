using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// DNS resolver used by the bounded crawler. DoH connects to fixed public
/// bootstrap addresses while TLS still authenticates the provider hostname.
/// Answers are cached in memory only and are never written to a log or disk.
/// </summary>
internal static class SecureDnsResolver
{
    private sealed record CacheEntry(DateTimeOffset ExpiresUtc, IPAddress[] Addresses);

    private static readonly object CacheGate = new();
    private static readonly Dictionary<string, CacheEntry> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient CloudflareClient = CreateClient(
        SecureDnsProvider.Cloudflare, [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("1.0.0.1")]);
    private static readonly HttpClient Quad9Client = CreateClient(
        SecureDnsProvider.Quad9, [IPAddress.Parse("9.9.9.9"), IPAddress.Parse("149.112.112.112")]);

    public static async Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        host = NormalizeHost(host);
        if (IPAddress.TryParse(host.Trim('[', ']'), out var literal)) return [literal];

        var settings = SettingsService.Current;
        if (settings.SecureDnsMode == SecureDnsMode.System)
            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);

        var cacheKey = settings.SecureDnsProvider + "|" + host;
        lock (CacheGate)
        {
            if (Cache.TryGetValue(cacheKey, out var cached) && cached.ExpiresUtc > DateTimeOffset.UtcNow)
                return cached.Addresses.ToArray();
        }

        try
        {
            var addresses = await QueryProviderAsync(host, settings.SecureDnsProvider, cancellationToken)
                .ConfigureAwait(false);
            if (addresses.Length == 0)
                throw new HttpRequestException("Зашифрованный DNS не вернул адресов для узла.");
            lock (CacheGate)
            {
                Cache[cacheKey] = new CacheEntry(DateTimeOffset.UtcNow.AddMinutes(5), addresses);
                TrimExpiredEntries();
            }
            return addresses.ToArray();
        }
        catch (OperationCanceledException) when (settings.SecureDnsMode == SecureDnsMode.Automatic &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (settings.SecureDnsMode == SecureDnsMode.Automatic &&
                                   (ex is HttpRequestException or IOException or SocketException))
        {
            return await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<IPAddress[]> QueryProviderAsync(string host, SecureDnsProvider provider,
        CancellationToken cancellationToken)
    {
        var client = provider == SecureDnsProvider.Quad9 ? Quad9Client : CloudflareClient;
        var answers = await Task.WhenAll(
            QueryRecordAsync(client, host, 1, cancellationToken),
            QueryRecordAsync(client, host, 28, cancellationToken)).ConfigureAwait(false);
        return answers.SelectMany(x => x).Distinct().Take(17).ToArray();
    }

    private static async Task<IPAddress[]> QueryRecordAsync(HttpClient client, string host, ushort type,
        CancellationToken cancellationToken)
    {
        var id = checked((ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue + 1));
        var query = BuildQuery(id, host, type);
        using var content = new ByteArrayContent(query);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        using var request = new HttpRequestMessage(HttpMethod.Post, string.Empty) { Content = content };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > 65_535)
            throw new HttpRequestException("DoH-ответ превышает допустимый размер.");
        var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (payload.Length > 65_535)
            throw new HttpRequestException("DoH-ответ превышает допустимый размер.");
        return ParseResponse(payload, id);
    }

    private static byte[] BuildQuery(ushort id, string host, ushort type)
    {
        using var stream = new MemoryStream(512);
        WriteUInt16(stream, id);
        WriteUInt16(stream, 0x0100);
        WriteUInt16(stream, 1);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        WriteUInt16(stream, 0);
        foreach (var label in host.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new HttpRequestException("Недопустимое DNS-имя.");
            stream.WriteByte(checked((byte)bytes.Length));
            stream.Write(bytes);
        }
        stream.WriteByte(0);
        WriteUInt16(stream, type);
        WriteUInt16(stream, 1);
        return stream.ToArray();
    }

    private static IPAddress[] ParseResponse(byte[] payload, ushort expectedId)
    {
        if (payload.Length < 12 || ReadUInt16(payload, 0) != expectedId)
            throw new HttpRequestException("Получен некорректный DoH-ответ.");
        var flags = ReadUInt16(payload, 2);
        if ((flags & 0x8000) == 0 || (flags & 0x000f) != 0)
            throw new HttpRequestException("Зашифрованный DNS отклонил запрос.");

        var questionCount = ReadUInt16(payload, 4);
        var answerCount = ReadUInt16(payload, 6);
        var offset = 12;
        for (var index = 0; index < questionCount; index++)
        {
            offset = SkipName(payload, offset);
            EnsureAvailable(payload, offset, 4);
            offset += 4;
        }

        var result = new List<IPAddress>();
        for (var index = 0; index < answerCount; index++)
        {
            offset = SkipName(payload, offset);
            EnsureAvailable(payload, offset, 10);
            var type = ReadUInt16(payload, offset);
            var recordClass = ReadUInt16(payload, offset + 2);
            var length = ReadUInt16(payload, offset + 8);
            offset += 10;
            EnsureAvailable(payload, offset, length);
            if (recordClass == 1 && type == 1 && length == 4)
                result.Add(new IPAddress(payload.AsSpan(offset, 4)));
            else if (recordClass == 1 && type == 28 && length == 16)
                result.Add(new IPAddress(payload.AsSpan(offset, 16)));
            offset += length;
        }
        return result.ToArray();
    }

    private static int SkipName(byte[] payload, int offset)
    {
        for (var labels = 0; labels < 128; labels++)
        {
            EnsureAvailable(payload, offset, 1);
            var length = payload[offset];
            if (length == 0) return offset + 1;
            if ((length & 0xc0) == 0xc0)
            {
                EnsureAvailable(payload, offset, 2);
                return offset + 2;
            }
            if (length > 63) throw new HttpRequestException("Некорректное DNS-имя в DoH-ответе.");
            EnsureAvailable(payload, offset + 1, length);
            offset += length + 1;
        }
        throw new HttpRequestException("DNS-имя в DoH-ответе слишком длинное.");
    }

    private static HttpClient CreateClient(SecureDnsProvider provider, IPAddress[] bootstrapAddresses)
    {
        var endpoint = new Uri(SecureNetworkConfigurationService.GetTemplate(provider));
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5),
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            ConnectCallback = (context, token) => ConnectBootstrapAsync(
                context, endpoint.Host, bootstrapAddresses, token)
        };
        return new HttpClient(handler)
        {
            BaseAddress = endpoint,
            Timeout = TimeSpan.FromSeconds(8)
        };
    }

    private static async ValueTask<Stream> ConnectBootstrapAsync(SocketsHttpConnectionContext context,
        string expectedHost, IPAddress[] addresses, CancellationToken cancellationToken)
    {
        if (!context.DnsEndPoint.Host.Equals(expectedHost, StringComparison.OrdinalIgnoreCase) ||
            context.DnsEndPoint.Port != 443)
            throw new HttpRequestException("DoH bootstrap заблокировал неожиданный адрес назначения.");

        Exception? lastError = null;
        foreach (var address in addresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, 443), cancellationToken).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (OperationCanceledException)
            {
                socket.Dispose();
                throw;
            }
            catch (SocketException ex)
            {
                lastError = ex;
                socket.Dispose();
            }
        }
        throw new HttpRequestException("Не удалось подключиться к закреплённому DoH-провайдеру.", lastError);
    }

    private static string NormalizeHost(string host)
    {
        host = host.Trim().TrimEnd('.');
        if (host.Length is 0 or > 253) throw new HttpRequestException("Недопустимое DNS-имя.");
        try { return new IdnMapping().GetAscii(host).ToLowerInvariant(); }
        catch (ArgumentException ex) { throw new HttpRequestException("Недопустимое DNS-имя.", ex); }
    }

    private static void TrimExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in Cache.Where(x => x.Value.ExpiresUtc <= now).Select(x => x.Key).ToArray())
            Cache.Remove(key);
        if (Cache.Count <= 256) return;
        foreach (var key in Cache.OrderBy(x => x.Value.ExpiresUtc).Take(Cache.Count - 256)
                     .Select(x => x.Key).ToArray())
            Cache.Remove(key);
    }

    private static void EnsureAvailable(byte[] payload, int offset, int length)
    {
        if (offset < 0 || length < 0 || offset > payload.Length - length)
            throw new HttpRequestException("Обрезанный DoH-ответ.");
    }

    private static ushort ReadUInt16(byte[] value, int offset)
    {
        EnsureAvailable(value, offset, 2);
        return BinaryPrimitives.ReadUInt16BigEndian(value.AsSpan(offset, 2));
    }

    private static void WriteUInt16(Stream stream, ushort value)
    {
        Span<byte> bytes = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, value);
        stream.Write(bytes);
    }
}
