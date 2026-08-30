using System.Net.NetworkInformation;

namespace NexusMonach.Services.Warp;

/// <summary>Состояние официального клиента Cloudflare WARP на машине.</summary>
public enum WarpStatus
{
    /// <summary>Клиент не установлен — адаптера Cloudflare нет.</summary>
    NotInstalled,
    /// <summary>Туннель WARP поднят: системная обёртка, пригодная для слоя.</summary>
    Connected,
    /// <summary>Клиент есть (адаптер присутствует), туннель опущен.</summary>
    Disconnected,
    /// <summary>Статус ещё не прочитан.</summary>
    Unknown
}

/// <summary>
/// Детектор официального клиента Cloudflare WARP — по его сетевому адаптеру,
/// без запуска чужих процессов и без своего сетевого стека. Перечисление
/// адаптеров — ДОРОГАЯ операция: под фильтрами сторонних VPN-драйверов она
/// умеет виснуть секундами, поэтому статус вычисляется в фоне и кэшируется.
/// Путь горячего соединения никогда не касается адаптеров напрямую.
/// WARP — бесплатный системный туннель к сети Cloudflare: слот доступности,
/// НЕ анонимности (Cloudflare видит трафик и логирует). Управление — в самом
/// клиенте WARP; браузер читает состояние и заворачивает в туннель слой.
/// </summary>
public static class WarpService
{
    private static readonly object Sync = new();
    private static WarpStatus _cached = WarpStatus.Unknown;
    private static DateTimeOffset _cachedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(5);

    /// <summary>Кэшированный статус; протухший — обновляется в фоне.</summary>
    public static WarpStatus Status
    {
        get
        {
            lock (Sync)
            {
                if (DateTimeOffset.UtcNow - _cachedAt < CacheTtl) return _cached;
                if (_cachedAt == DateTimeOffset.MinValue)
                {
                    // Первый опрос — синхронно (старт), дальше только фон.
                    Refresh();
                    return _cached;
                }
                _ = Task.Run(Refresh);
                return _cached;
            }
        }
    }

    public static bool IsInstalled => Status != WarpStatus.NotInstalled;

    public static bool IsConnected => Status == WarpStatus.Connected;

    private static void Refresh()
    {
        try
        {
            var warpUp = NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.Name.ToLowerInvariant().Contains("cloudflarewarp") ||
                             ni.Description.ToLowerInvariant().Contains("cloudflarewarp"))
                .ToList();
            _cached = ClassifyAdapter(warpUp
                .Select(ni => (ni.OperationalStatus == OperationalStatus.Up, true))
                .ToList());
        }
        catch
        {
            _cached = WarpStatus.Unknown;
        }
        _cachedAt = DateTimeOffset.UtcNow;
    }

    /// <summary>Чистая функция классификации — проверяется юнит-тестами.</summary>
    internal static WarpStatus ClassifyAdapter(
        IReadOnlyCollection<(bool IsUp, bool IsWarp)> adapters)
    {
        var warp = adapters.Where(a => a.IsWarp).ToList();
        if (warp.Count == 0) return WarpStatus.NotInstalled;
        return warp.Any(a => a.IsUp) ? WarpStatus.Connected : WarpStatus.Disconnected;
    }
}
