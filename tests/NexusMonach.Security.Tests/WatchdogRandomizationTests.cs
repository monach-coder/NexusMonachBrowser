using NexusMonach.Services;
using NexusMonach.Services.Tor;
using NexusMonach.Services.Vless;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Рандомизация Дозора: порты ловушек, тарпит и баннеры должны быть
/// непредсказуемы между сессиями — тогда знание открытого кода не даёт
/// сканеру бесплатного фильтра (принцип: механизм публичен, энтропия нет).
/// </summary>
public sealed class WatchdogRandomizationTests
{
    [Fact]
    public void SessionPorts_AreSevenDistinct_FromPool()
    {
        for (var run = 0; run < 20; run++)
        {
            var selected = NetworkWatchdog.SelectHoneypotPorts();
            Assert.Equal(7, selected.Length);
            Assert.Equal(selected.Length, selected.Distinct().Count());
            Assert.All(selected, port => Assert.Contains(port, NetworkWatchdog.HoneypotPool));
        }
    }

    [Fact]
    public void SessionPorts_VaryBetweenSessions()
    {
        // C(22,7) ≈ 170 тысяч комбинаций — две сессии почти наверняка
        // различаются; проверяем серию, а не пару, чтобы не флапал.
        var selections = Enumerable.Range(0, 6)
            .Select(_ => string.Join(',', NetworkWatchdog.SelectHoneypotPorts().Order()))
            .ToHashSet();
        Assert.True(selections.Count > 1, "Выбор портов должен отличаться между сессиями");
    }

    [Fact]
    public void Pool_ExcludesAdministrativeAndSystemPorts()
    {
        // Ловушки не должны занимать порты, которые пользователь может
        // включить позже (RDP/VNC/SMB), и порты привилегированного диапазона.
        Assert.DoesNotContain(3389, NetworkWatchdog.HoneypotPool);
        Assert.DoesNotContain(5900, NetworkWatchdog.HoneypotPool);
        Assert.All(NetworkWatchdog.HoneypotPool, port => Assert.True(port > 1024));
    }

    [Fact]
    public void TarpitSeconds_BoundedAndRandom()
    {
        var seen = new HashSet<int>();
        for (var i = 0; i < 30; i++)
        {
            var seconds = NetworkWatchdog.PickTarpitSeconds();
            Assert.InRange(seconds, 20, 60);
            seen.Add(seconds);
        }
        Assert.True(seen.Count > 5, "Длительность тарпита должна варьироваться");
    }

    [Theory]
    [InlineData(2222, "SSH-2.0-OpenSSH_")]
    [InlineData(6379, "-ERR")]
    [InlineData(27017, "MongoDB ")]
    [InlineData(9200, "elasticsearch")]
    [InlineData(8080, "Server:")]
    [InlineData(3000, "nginx/")]
    [InlineData(8081, "Apache/")]
    public void Banner_MatchesPortFamily(int port, string marker)
    {
        var banner = NetworkWatchdog.PickBanner(port);
        Assert.Contains(marker, banner, StringComparison.Ordinal);
    }

    [Fact]
    public void Banner_VersionsVary()
    {
        // Версии баннеров рандомизированы: сигнатура ловушки не должна
        // повторяться между подключениями.
        var banners = Enumerable.Range(0, 20)
            .Select(_ => NetworkWatchdog.PickBanner(8080))
            .ToHashSet();
        Assert.True(banners.Count > 1, "Версия баннера должна варьироваться");
    }

    [Fact]
    public void TransportSocksBasePort_InSafeRange_NoConflicts()
    {
        // Базовый порт транспорта случаен на запуск процесса (= на сессию)
        // и не пересекается с портами Тора (9050–9052) и приманками Дозора.
        var port = VlessRuntime.PreferredSocksPort;
        Assert.InRange(port, 9300, 9699);
        Assert.DoesNotContain(port, NetworkWatchdog.HoneypotPool);
    }
}
