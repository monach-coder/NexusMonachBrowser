using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;

namespace NexusMonach.Services.Tor;

/// <summary>Один работающий релей Тора из реестра Tor Metrics.</summary>
public sealed record RelayCandidate(string Address, int Port, string Fingerprint)
{
    /// <summary>Строка для torrc: обычный релей как мост.</summary>
    public string ToBridgeLine() => $"{Address}:{Port} {Fingerprint}";
}

/// <summary>
/// Поисковик релейных мостов: метод ValdikSS — публичных мостов сотни (их
/// блокируют первыми), а работающих релеев Тора тысячи, и цензору не выкурить
/// весь длинный хвост. Список берётся с официального реестра Tor Metrics
/// (onionoo), при недоступности — с зеркал; случайной рукой выбираются
/// кандидаты и живой TCP-пробой отбираются достижимые. Найденные вписываются
/// в пул ротации как обычные строки Bridge.
/// </summary>
public static class RelayBridgeFinder
{
    private static readonly HttpClient Client = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    /// <summary>Источники реестра: официальный и зеркала ValdikSS.</summary>
    internal static readonly string[] RegistryUrls =
    [
        "https://onionoo.torproject.org/details?type=relay&running=true&fields=fingerprint,or_addresses",
        "https://github.com/ValdikSS/tor-onionoo-mirror/raw/master/details-running-relays-fingerprint-address-only.json",
        "https://bitbucket.org/ValdikSS/tor-onionoo-mirror/raw/master/details-running-relays-fingerprint-address-only.json"
    ];

    /// <summary>Сеть сканирования: только IPv4-адреса релеев.</summary>
    public static async Task<IReadOnlyList<RelayCandidate>> FetchRelaysAsync(
        CancellationToken ct = default)
    {
        foreach (var url in RegistryUrls)
        {
            try
            {
                using var response = await Client.GetAsync(url, ct);
                response.EnsureSuccessStatusCode();
                var json = await response.Content.ReadAsStringAsync(ct);
                var relays = ParseRegistry(json);
                if (relays.Count > 0) return relays;
            }
            catch
            {
                // Этот источник недоступен — пробуем зеркало.
            }
        }
        return [];
    }

    /// <summary>
    /// Живой поиск: случайно берём кандидатов и проверяем достижимость
    /// TCP-пробой, пока не наберём goal работающих (или не кончится бюджет).
    /// </summary>
    public static async Task<IReadOnlyList<RelayCandidate>> FindWorkingAsync(
        IReadOnlyList<RelayCandidate> relays,
        int goal = 3,
        int attemptsBudget = 40,
        Func<int, int, Task>? onProgress = null,
        CancellationToken ct = default)
    {
        var working = new List<RelayCandidate>();
        var pool = relays.OrderBy(_ => RandomNumberGenerator.GetInt32(int.MaxValue)).ToList();
        var tried = 0;
        foreach (var candidate in pool)
        {
            if (working.Count >= goal || tried >= attemptsBudget || ct.IsCancellationRequested)
                break;
            tried++;
            if (onProgress is not null) await onProgress(tried, working.Count);
            if (await IsReachableAsync(candidate, ct))
                working.Add(candidate);
        }
        return working;
    }

    /// <summary>TCP-проба достижимости ORPort релея.</summary>
    public static async Task<bool> IsReachableAsync(RelayCandidate relay, CancellationToken ct = default)
    {
        try
        {
            using var tcp = new TcpClient();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
            await tcp.ConnectAsync(System.Net.IPAddress.Parse(relay.Address), relay.Port, linked.Token);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Разбор реестра onionoo: релеи с IPv4 OR-адресами. Чистая функция для тестов.
    /// Формат зеркал ValdikSS совместим: тот же JSON с relays[].
    /// </summary>
    internal static IReadOnlyList<RelayCandidate> ParseRegistry(string json)
    {
        var result = new List<RelayCandidate>();
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("relays", out var relays) ||
                relays.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var relay in relays.EnumerateArray())
            {
                if (!relay.TryGetProperty("fingerprint", out var fp)) continue;
                var fingerprint = fp.GetString();
                if (string.IsNullOrWhiteSpace(fingerprint)) continue;
                if (!relay.TryGetProperty("or_addresses", out var addresses) ||
                    addresses.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var address in addresses.EnumerateArray())
                {
                    var raw = address.GetString();
                    if (string.IsNullOrWhiteSpace(raw)) continue;
                    var parsed = ParseOrAddress(raw);
                    if (parsed is not null)
                    {
                        result.Add(new RelayCandidate(parsed.Value.address, parsed.Value.port, fingerprint));
                        break; // достаточно одного IPv4-адреса релея
                    }
                }
            }
        }
        catch
        {
            // Битый реестр — пустой результат, вызывающий честно сообщит.
        }
        return result;
    }

    /// <summary>Разбор or_address: «1.2.3.4:9001» или «[::1]:9001» (IPv6 мимо).</summary>
    internal static (string address, int port)? ParseOrAddress(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
            return null; // IPv6 в текущей сети только топит — берём IPv4
        var separator = trimmed.LastIndexOf(':');
        if (separator <= 0 || separator == trimmed.Length - 1) return null;
        if (!int.TryParse(trimmed[(separator + 1)..], out var port)) return null;
        var host = trimmed[..separator];
        if (!System.Net.IPAddress.TryParse(host, out var ip) ||
            ip.AddressFamily != AddressFamily.InterNetwork)
            return null;
        return (host, port);
    }
}
