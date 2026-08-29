using NexusMonach.Services.Chain;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Встроенный маршрутизатор цепочки: чистая логика выбора маршрута и
/// построение SOCKS5-запросов. Сетевая часть покрыта самопроверкой браузера.
/// </summary>
public class ChainRouterTests
{
    [Fact]
    public void PickRoute_TorOnlyWhenInChainAndWrapped()
    {
        Assert.Equal(ChainRoute.Tor, ChainRouterService.PickRoute(
            torInChain: true, torWrapped: true,
            vlessEnabled: true, vlessRunning: true,
            customProxyEnabled: true));

        // Слой в цепочке, но без обёртки — напрямую он не выходит.
        Assert.Equal(ChainRoute.Transport, ChainRouterService.PickRoute(
            torInChain: true, torWrapped: false,
            vlessEnabled: true, vlessRunning: true,
            customProxyEnabled: true));

        // Слой исключён из цепочки — транспорт не перекрывается.
        Assert.Equal(ChainRoute.Transport, ChainRouterService.PickRoute(
            torInChain: false, torWrapped: true,
            vlessEnabled: true, vlessRunning: true,
            customProxyEnabled: true));
    }

    [Fact]
    public void PickRoute_CustomProxyWhenNoTunnel()
    {
        Assert.Equal(ChainRoute.CustomProxy, ChainRouterService.PickRoute(
            torInChain: true, torWrapped: false,
            vlessEnabled: false, vlessRunning: false,
            customProxyEnabled: true));

        // Выключенный тумблер транспорта не должен занимать маршрут.
        Assert.Equal(ChainRoute.CustomProxy, ChainRouterService.PickRoute(
            torInChain: false, torWrapped: false,
            vlessEnabled: false, vlessRunning: true,
            customProxyEnabled: true));
    }

    [Fact]
    public void PickRoute_DirectIsTheBuiltInDefault()
    {
        Assert.Equal(ChainRoute.Direct, ChainRouterService.PickRoute(
            torInChain: true, torWrapped: false,
            vlessEnabled: false, vlessRunning: false,
            customProxyEnabled: false));

        Assert.Equal(ChainRoute.Direct, ChainRouterService.PickRoute(
            torInChain: false, torWrapped: false,
            vlessEnabled: false, vlessRunning: false,
            customProxyEnabled: false));
    }

    [Fact]
    public void BuildConnectRequest_DomainKeepsRemoteDns()
    {
        var request = ChainRouterService.BuildConnectRequest("example.org", 443);

        Assert.Equal(5, request[0]);
        Assert.Equal(1, request[1]);
        Assert.Equal(0, request[2]);
        Assert.Equal(3, request[3]); // ATYP: домен
        Assert.Equal(11, request[4]); // длина example.org
        Assert.Equal("example.org",
            System.Text.Encoding.ASCII.GetString(request, 5, 11));
        Assert.Equal(443, (request[^2] << 8) | request[^1]);
    }

    [Fact]
    public void BuildConnectRequest_Ipv4UsesAddressType()
    {
        var request = ChainRouterService.BuildConnectRequest("127.0.0.1", 9050);

        Assert.Equal(1, request[3]); // ATYP: IPv4
        Assert.Equal(4 + 4 + 2, request.Length);
        Assert.Equal(127, request[4]);
        Assert.Equal(0, request[5]);
        Assert.Equal(0, request[6]);
        Assert.Equal(1, request[7]);
        Assert.Equal(9050, (request[^2] << 8) | request[^1]);
    }

    [Fact]
    public void BuildConnectRequest_RejectsBadDomain()
    {
        Assert.Throws<ArgumentException>(() =>
            ChainRouterService.BuildConnectRequest("", 80));
    }
}
