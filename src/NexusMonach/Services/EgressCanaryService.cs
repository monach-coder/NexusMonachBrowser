using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;

namespace NexusMonach.Services;

/// <summary>
/// Канарейка цепочки: периодически соединяется через активный транспорт
/// (Xray или маршрут) с известным хостом и запоминает TLS-сертификат.
/// Смена сертификата посреди сессии — признак перехвата в туннеле:
/// честные ротации редки, а подмена прозрачна для пользователя.
/// Внешний запрос один и тот же — метаданные не разрастаются.
/// </summary>
public static class EgressCanaryService
{
    private const string CanaryHost = "api.github.com";
    private const int CanaryPort = 443;
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(10);

    private static Timer? _timer;
    private static string? _pinnedCertHash;
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static void Start()
    {
        if (_timer is not null) return;
        _timer = new Timer(async _ => await CheckAsync(), null,
            TimeSpan.FromSeconds(30), Interval);
    }

    public static void Stop() => _timer?.Dispose();

    private static async Task CheckAsync()
    {
        if (!await Gate.WaitAsync(0)) return;
        try
        {
            var (proxyHost, proxyPort) = ActiveChainProxy();
            if (proxyPort == 0) return;

            var hash = await FetchCertHashAsync(proxyHost, proxyPort);
            if (hash is null) return;

            if (_pinnedCertHash is null)
            {
                _pinnedCertHash = hash;
                CrashReportService.AddBreadcrumb("canary", "pinned-" + hash[..12]);
                return;
            }
            if (!string.Equals(_pinnedCertHash, hash, StringComparison.Ordinal))
            {
                CrashReportService.AddBreadcrumb("canary", "cert-changed");
                Ui.Post(() => VoiceAssistantService.Announce(
                    "Внимание: сертификат канарейки изменился посреди сессии — возможно, перехват в туннеле. Проверьте маршрут.",
                    VoiceAnnouncementPriority.Critical));
                // Новый пин после предупреждения: повторные ротации не спамят.
                _pinnedCertHash = hash;
            }
        }
        catch (OperationCanceledException)
        {
            // Таймаут сокета — рутина проверки, а не сбой браузера: рапорт
            // не пишем, иначе CrashVault замусоривается каждые 10 минут.
            CrashReportService.AddBreadcrumb("canary", "probe-timeout");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or HttpRequestException)
        {
            // Сеть молчит или транспорт не отвечает — норма для этой модели
            // угроз: канарейка просто повторит проверку через интервал.
            CrashReportService.AddBreadcrumb("canary", "unreachable");
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("canary", "check", ex);
        }
        finally { Gate.Release(); }
    }

    /// <summary>Активный прокси цепочки: транспорт при поднятом сервере,
    /// иначе маршрут — либо ничего (проверка не выполняется).</summary>
    internal static (string Host, int Port) ActiveChainProxy()
    {
        if (Services.Vless.VlessRuntime.IsRunning)
            return ("127.0.0.1", Services.Vless.VlessRuntime.SocksPort);
        if (Services.Tor.TorService.IsRunning)
            return ("127.0.0.1", Services.Tor.TorService.SocksPort);
        return (string.Empty, 0);
    }

    /// <summary>
    /// Соединяется с канарейкой через SOCKS5-прокси и возвращает хеш
    /// TLS-сертификата (SHA-256 по всему сертификату).
    /// </summary>
    internal static async Task<string?> FetchCertHashAsync(string proxyHost, int proxyPort)
    {
        using var client = new TcpClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        await client.ConnectAsync(proxyHost, proxyPort, cts.Token);
        var stream = client.GetStream();

        // SOCKS5: приветствие без аутентификации.
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, cts.Token);
        var greeting = new byte[2];
        await ReadExactAsync(stream, greeting, cts.Token);
        if (greeting[0] != 5) return null;

        // CONNECT по доменному имени: резолв делает прокси, а не машина.
        var host = System.Text.Encoding.ASCII.GetBytes(CanaryHost);
        var request = new byte[7 + host.Length];
        request[0] = 5; request[1] = 1; request[2] = 0; request[3] = 3;
        request[4] = (byte)host.Length;
        host.CopyTo(request, 5);
        request[5 + host.Length] = (byte)(CanaryPort >> 8);
        request[6 + host.Length] = (byte)(CanaryPort & 0xFF);
        await stream.WriteAsync(request, cts.Token);

        var response = new byte[4];
        await ReadExactAsync(stream, response, cts.Token);
        if (response[1] != 0) return null; // прокси отказал
        // За заголовком — адрес сервера (формат по ATYP) и порт; пропускаем.
        var addressType = response[3];
        var skip = addressType switch { 1 => 4, 4 => 16, _ => 0 };
        if (addressType == 3)
        {
            var length = new byte[1];
            await ReadExactAsync(stream, length, cts.Token);
            skip = length[0];
        }
        if (skip > 0)
            await ReadExactAsync(stream, new byte[skip + 2], cts.Token);

        // TLS-рукопожатие сквозь туннель и хеш сертификата.
        using var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = CanaryHost
        }, cts.Token);
        var cert = new X509Certificate2(ssl.RemoteCertificate!);
        return Convert.ToHexString(SHA256.HashData(cert.RawData)).ToLowerInvariant();
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer,
        CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = await stream.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (chunk == 0) throw new IOException("SOCKS-прокси закрыл соединение.");
            read += chunk;
        }
    }
}
