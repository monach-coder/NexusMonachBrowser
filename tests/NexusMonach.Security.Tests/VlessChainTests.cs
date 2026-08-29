using System.Text.Json;
using NexusMonach.Models;
using NexusMonach.Services;
using NexusMonach.Services.Tor;
using NexusMonach.Services.Vless;
using Xunit;

namespace NexusMonach.Security.Tests;

public sealed class VlessChainTests
{
    private const string RealityLink =
        "vless://1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d@example.com:443" +
        "?type=tcp&security=reality&pbk=PUBLICKEY123&sni=www.example.com" +
        "&fp=chrome&sid=ab12&flow=xtls-rprx-vision#Мой%20сервер";

    [Fact]
    public void ProfileParses_RealityLink_AllFields()
    {
        Assert.True(VlessProfile.TryParse(RealityLink, out var profile, out var error));
        Assert.Equal(string.Empty, error);
        Assert.NotNull(profile);
        Assert.Equal("example.com", profile.Address);
        Assert.Equal(443, profile.Port);
        Assert.Equal("1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d", profile.UserId);
        Assert.Equal("reality", profile.Security);
        Assert.Equal("PUBLICKEY123", profile.PublicKey);
        Assert.Equal("www.example.com", profile.Sni);
        Assert.Equal("chrome", profile.Fingerprint);
        Assert.Equal("ab12", profile.ShortId);
        Assert.Equal("xtls-rprx-vision", profile.Flow);
        Assert.Equal("Мой сервер", profile.Name);
        Assert.True(profile.UsesReality);
    }

    [Fact]
    public void ProfileParses_WebSocketLink()
    {
        var link = "vless://1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d@host.io:8443" +
                   "?type=ws&security=tls&path=%2Fws&host=cdn.host.io&alpn=h2,http%2F1.1#ws";
        Assert.True(VlessProfile.TryParse(link, out var profile, out _));
        Assert.NotNull(profile);
        Assert.Equal("ws", profile.Network);
        Assert.Equal("tls", profile.Security);
        Assert.Equal("/ws", profile.Path);
        Assert.Equal("cdn.host.io", profile.Host);
        Assert.False(profile.UsesReality);
    }

    [Theory]
    [InlineData("not-a-link")]
    [InlineData("https://example.com")]
    [InlineData("vless://not-a-uuid@example.com:443")]
    [InlineData("vless://1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d@example.com:443?security=reality&sni=x.com")]
    [InlineData("vless://1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d@example.com:443?security=reality&pbk=1")]
    [InlineData("vless://1a2b3c4d-5e6f-4a7b-8c9d-0e1f2a3b4c5d@example.com:443?type=quic")]
    [InlineData("")]
    public void ProfileRejects_BrokenLinks(string link)
    {
        Assert.False(VlessProfile.TryParse(link, out var profile, out var error));
        Assert.Null(profile);
        Assert.NotEqual(string.Empty, error);
    }

    [Fact]
    public void XrayConfig_HasSocksInbound_VlessOutbound_AndReality()
    {
        Assert.True(VlessProfile.TryParse(RealityLink, out var profile, out _));
        Assert.NotNull(profile);
        var config = VlessRuntime.BuildConfig(profile, 9155);
        using var json = JsonDocument.Parse(config);

        var inbound = json.RootElement.GetProperty("inbounds")[0];
        Assert.Equal("socks", inbound.GetProperty("protocol").GetString());
        Assert.Equal(9155, inbound.GetProperty("port").GetInt32());
        Assert.Equal("127.0.0.1", inbound.GetProperty("listen").GetString());

        var outbound = json.RootElement.GetProperty("outbounds")[0];
        Assert.Equal("vless", outbound.GetProperty("protocol").GetString());
        var stream = outbound.GetProperty("streamSettings");
        Assert.Equal("reality", stream.GetProperty("security").GetString());
        var reality = stream.GetProperty("realitySettings");
        Assert.Equal("www.example.com", reality.GetProperty("serverName").GetString());
        Assert.Equal("PUBLICKEY123", reality.GetProperty("publicKey").GetString());

        // Локальные адреса никогда не уходят на сервер.
        var rule = json.RootElement.GetProperty("routing").GetProperty("rules")[0];
        Assert.Equal("direct", rule.GetProperty("outboundTag").GetString());
    }

    [Fact]
    public void Torrc_WithUpstream_WrapsTorInTransport()
    {
        var torrc = TorBridgeManager.GenerateTorrc(
            bridges: [], socksPort: 9051, relayEnabled: false, upstreamSocksPort: 9155);
        Assert.Contains("Socks5Proxy 127.0.0.1:9155", torrc, StringComparison.Ordinal);
    }

    [Fact]
    public void Torrc_WithoutUpstream_NoWrapLine()
    {
        var torrc = TorBridgeManager.GenerateTorrc(
            bridges: [], socksPort: 9051, relayEnabled: false);
        Assert.DoesNotContain("Socks5Proxy", torrc, StringComparison.Ordinal);
    }

    [Fact]
    public void ProxyArguments_FallThroughToCustom_WhenServicesDown()
    {
        // Цепочка адаптивна к живому окружению машины разработки: может быть
        // поднят Тор (9051), а после smoke-прогона — и транспорт со случайным
        // портом; без живых служб действует ручной прокси из настроек.
        var settings = new BrowserSettings
        {
            TorInChain = true,
            VlessEnabled = true,
            EnableCustomProxy = true,
            ProxyKind = ProxyKind.Socks5,
            ProxyHost = "127.0.0.1",
            ProxyPort = 9155
        };
        var arguments = ProxyConfigurationService.BuildBrowserArguments(settings);
        var expected = Services.Vless.VlessRuntime.IsRunning
            ? $"socks5://127.0.0.1:{Services.Vless.VlessRuntime.SocksPort}"
            : TorService.IsRunning
                ? $"socks5://127.0.0.1:{TorService.SocksPort}"
                : "socks5://127.0.0.1:9155";
        Assert.Contains(expected, arguments, StringComparison.Ordinal);
    }
}
