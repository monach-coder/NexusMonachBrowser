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
    Disconnected
}

/// <summary>
/// Детектор официального клиента Cloudflare WARP — по его сетевому адаптеру,
/// без запуска чужих процессов и без своего сетевого стека. WARP — бесплатный
/// системный туннель к сети Cloudflare: в нашей цепочке он занимает слот
/// обёртки, как системный VPN — годится, когда нет своего сервера. Но это НЕ
/// анонимность: Cloudflare видит весь трафик и логирует; слот — доступность.
/// Подключением управляет сам клиент WARP (иконка в трее) — браузер читает
/// состояние и заворачивает в туннель анонимный слой.
/// </summary>
public static class WarpService
{
    /// <summary>Живой статус по адаптерам машины.</summary>
    public static WarpStatus Status =>
        ClassifyAdapter(NetworkInterface.GetAllNetworkInterfaces()
            .Where(ni => ni.Name.ToLowerInvariant().Contains("cloudflarewarp") ||
                         ni.Description.ToLowerInvariant().Contains("cloudflarewarp"))
            .ToList());

    public static bool IsInstalled => Status != WarpStatus.NotInstalled;

    public static bool IsConnected => Status == WarpStatus.Connected;

    /// <summary>Чистая функция классификации — проверяется юнит-тестами.</summary>
    internal static WarpStatus ClassifyAdapter(
        IReadOnlyCollection<(bool IsUp, bool IsWarp)> adapters)
    {
        var warp = adapters.Where(a => a.IsWarp).ToList();
        if (warp.Count == 0) return WarpStatus.NotInstalled;
        return warp.Any(a => a.IsUp) ? WarpStatus.Connected : WarpStatus.Disconnected;
    }

    private static WarpStatus ClassifyAdapter(List<NetworkInterface> warpAdapters) =>
        ClassifyAdapter(warpAdapters.Select(ni => (ni.OperationalStatus == OperationalStatus.Up, true))
            .ToList());
}
