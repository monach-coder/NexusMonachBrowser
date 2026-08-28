using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NexusMonach.Services.Vless;

public enum VlessState { NotInstalled, Stopped, Starting, Connected, Failed }

/// <summary>
/// Управление локальным транспортом VLESS: генерирует конфиг для Xray
/// из профиля пользователя, запускает процесс, отдаёт браузеру и Тору
/// локальный SOCKS5-порт. Процесс стартует без аргументов — конфиг
/// лежит в рабочей папке под стандартным именем config.json, поэтому
/// пользовательский ввод в командную строку не попадает в принципе.
/// Транспорт умирает вместе с браузером (ProcessNursery); падение
/// транспорта уведомляет подписчиков, чтобы цепочка «Тор через сервер»
/// перестроилась без мёртвого прокси.
/// </summary>
public static class VlessRuntime
{
    /// <summary>
    /// Базовый SOCKS-порт транспорта: случайный на сессию, чтобы по одному
    /// открытому порту нельзя было опознать «браузер Nexus» снаружи.
    /// Диапазон не пересекается с портами Тора и приманками Дозора.
    /// </summary>
    public static int PreferredSocksPort { get; } =
        System.Security.Cryptography.RandomNumberGenerator.GetInt32(9300, 9700);
    private const int PortAttempts = 20;
    private const string ConfigFileName = "config.json";

    private static readonly object Gate = new();
    private static Process? _process;
    private static bool _intentionalStop;

    /// <summary>Транспорт упал не по команде пользователя — подписчики (Тор) перестраивают цепочку.</summary>
    public static event Action? TransportLost;

    public static int SocksPort { get; private set; } = PreferredSocksPort;
    public static bool IsRunning => Probe(SocksPort);
    public static string? ActiveProfileName { get; private set; }

    /// <summary>Путь к xray.exe в установке или null, если транспорт не доставлен.</summary>
    public static string? FindXray()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "xray", "xray.exe");
        return File.Exists(local) ? local : null;
    }

    /// <summary>
    /// Запускает (или переиспользует) транспорт под указанный профиль.
    /// Профиль уже должен пройти VlessProfile.TryParse.
    /// </summary>
    public static async Task<VlessState> EnsureRunningAsync(
        VlessProfile profile, CancellationToken ct = default)
    {
        if (IsRunning && ActiveProfileName == profile.Name) return VlessState.Connected;
        Stop();

        var exe = FindXray();
        if (exe is null) return VlessState.NotInstalled;
        var directory = Path.GetDirectoryName(exe)!;
        SocksPort = PickFreePort();

        var configPath = Path.Combine(directory, ConfigFileName);
        await File.WriteAllTextAsync(configPath, BuildConfig(profile, SocksPort), ct);

        Process process;
        lock (Gate)
        {
            // Без аргументов: xray читает config.json из рабочей папки.
            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = directory
            };
            process = Process.Start(psi) ?? throw new InvalidOperationException("Транспорт не запустился.");
            ProcessNursery.Adopt(process);
            _intentionalStop = false;
            // Падение транспорта не должно ронять построенную поверх него
            // цепочку: подписчики (Тор) получат событие и перестроятся.
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) =>
            {
                if (_intentionalStop) return;
                CrashReportService.AddBreadcrumb("vless", "transport-lost");
                try { TransportLost?.Invoke(); } catch { }
            };
            _process = process;
            ActiveProfileName = profile.Name;
        }

        // Реальность поднимается быстро, но DNS+TCP до сервера могут занять секунды.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            if (process.HasExited)
            {
                CrashReportService.AddBreadcrumb("vless", "xray-exit-" + ReadErrorLogTail(directory));
                Stop();
                return VlessState.Failed;
            }
            if (Probe(SocksPort))
            {
                CrashReportService.AddBreadcrumb("vless", "socks-ready-" + SocksPort);
                return VlessState.Connected;
            }
            await Task.Delay(250, ct);
        }
        return VlessState.Starting;
    }

    /// <summary>Останавливает транспорт и стирает конфиг с адресом сервера.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            _intentionalStop = true;
            if (_process is { HasExited: false })
                try { _process.Kill(true); } catch { }
            _process = null;
            ActiveProfileName = null;
        }
        var exe = FindXray();
        if (exe is null) return;
        try { File.Delete(Path.Combine(Path.GetDirectoryName(exe)!, ConfigFileName)); }
        catch { /* конфиг уже стёрт или недоступен — не критично */ }
    }

    /// <summary>SOCKS5-хендшейк: 05 01 00 → 05 XX.</summary>
    internal static bool Probe(int port)
    {
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync(IPAddress.Loopback, port).Wait(800)) return false;
            var stream = client.GetStream();
            stream.Write(new byte[] { 5, 1, 0 }, 0, 3);
            var answer = new byte[2];
            return stream.Read(answer, 0, 2) == 2 && answer[0] == 5;
        }
        catch { return false; }
    }

    private static int PickFreePort()
    {
        for (var port = PreferredSocksPort; port < PreferredSocksPort + PortAttempts; port++)
        {
            try
            {
                var listener = new TcpListener(IPAddress.Loopback, port);
                listener.Start();
                listener.Stop();
                return port;
            }
            catch { /* порт занят — пробуем следующий */ }
        }
        return PreferredSocksPort;
    }

    private static string ReadErrorLogTail(string directory)
    {
        try
        {
            var log = Path.Combine(directory, "vless-xray.log");
            if (!File.Exists(log) || new FileInfo(log).Length == 0) return "no-log";
            using var reader = new StreamReader(File.Open(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite));
            var text = reader.ReadToEnd();
            return text.Length <= 400 ? text : text[^400..];
        }
        catch { return "log-read-failed"; }
    }

    /// <summary>
    /// Конфиг Xray: локальный SOCKS5-inbound, VLESS-outbound профиля,
    /// прямые маршруты для локальных адресов.
    /// </summary>
    internal static string BuildConfig(VlessProfile profile, int socksPort)
    {
        var user = new Dictionary<string, object?>
        {
            ["id"] = profile.UserId,
            ["encryption"] = profile.Encryption,
            ["level"] = 0
        };
        if (profile.Flow.Length > 0)
            user["flow"] = profile.Flow;

        var outbound = new Dictionary<string, object?>
        {
            ["tag"] = "proxy",
            ["protocol"] = "vless",
            ["settings"] = new Dictionary<string, object?>
            {
                ["vnext"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["address"] = profile.Address,
                        ["port"] = profile.Port,
                        ["users"] = new object[] { user }
                    }
                }
            },
            ["streamSettings"] = BuildStreamSettings(profile)
        };

        var config = new Dictionary<string, object?>
        {
            ["log"] = new Dictionary<string, object?>
            {
                ["loglevel"] = "warning",
                ["error"] = "vless-xray.log"
            },
            ["inbounds"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["tag"] = "socks-in",
                    ["listen"] = "127.0.0.1",
                    ["port"] = socksPort,
                    ["protocol"] = "socks",
                    ["settings"] = new Dictionary<string, object?> { ["auth"] = "noauth", ["udp"] = true },
                    ["sniffing"] = new Dictionary<string, object?>
                    {
                        ["enabled"] = true,
                        ["destOverride"] = new[] { "http", "tls" }
                    }
                }
            },
            ["outbounds"] = new object[]
            {
                outbound,
                new Dictionary<string, object?> { ["tag"] = "direct", ["protocol"] = "freedom" },
                new Dictionary<string, object?> { ["tag"] = "block", ["protocol"] = "blackhole" }
            },
            ["routing"] = new Dictionary<string, object?>
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = new object[]
                {
                    new Dictionary<string, object?>
                    {
                        ["type"] = "field",
                        ["ip"] = new[] { "geoip:private" },
                        ["outboundTag"] = "direct"
                    }
                }
            }
        };
        return JsonSerializer.Serialize(config, JsonOptions);
    }

    private static Dictionary<string, object?> BuildStreamSettings(VlessProfile profile)
    {
        var stream = new Dictionary<string, object?>
        {
            ["network"] = profile.Network,
            ["security"] = profile.Security
        };
        switch (profile.Security)
        {
            case "reality":
                stream["realitySettings"] = new Dictionary<string, object?>
                {
                    ["show"] = false,
                    ["fingerprint"] = profile.Fingerprint,
                    ["serverName"] = profile.Sni,
                    ["publicKey"] = profile.PublicKey,
                    ["shortId"] = profile.ShortId,
                    ["spiderX"] = profile.SpiderX.Length > 0 ? profile.SpiderX : "/"
                };
                break;
            case "tls":
                var tls = new Dictionary<string, object?>
                {
                    ["serverName"] = profile.Sni,
                    ["fingerprint"] = profile.Fingerprint
                };
                if (profile.Alpn.Length > 0)
                    tls["alpn"] = profile.Alpn.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                stream["tlsSettings"] = tls;
                break;
        }
        switch (profile.Network)
        {
            case "ws":
                var ws = new Dictionary<string, object?>
                {
                    ["path"] = profile.Path.Length > 0 ? profile.Path : "/"
                };
                if (profile.Host.Length > 0)
                    ws["headers"] = new Dictionary<string, object?> { ["Host"] = profile.Host };
                stream["wsSettings"] = ws;
                break;
            case "grpc":
                stream["grpcSettings"] = new Dictionary<string, object?>
                {
                    ["serviceName"] = profile.ServiceName
                };
                break;
            case "http":
                var http = new Dictionary<string, object?>
                {
                    ["path"] = profile.Path.Length > 0 ? profile.Path : "/"
                };
                if (profile.Host.Length > 0)
                    http["host"] = new[] { profile.Host };
                stream["httpSettings"] = http;
                break;
        }
        return stream;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}
