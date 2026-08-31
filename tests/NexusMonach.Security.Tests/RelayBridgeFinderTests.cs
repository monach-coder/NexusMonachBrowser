using NexusMonach.Services.Tor;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Поисковик релейных мостов: разбор реестра onionoo и or-адресов —
/// чистые функции, сеть в тестах не участвует.
/// </summary>
public class RelayBridgeFinderTests
{
    [Theory]
    [InlineData("1.2.3.4:9001", "1.2.3.4", 9001)]
    [InlineData("77.88.8.8:443", "77.88.8.8", 443)]
    public void ParseOrAddress_Ipv4(string raw, string host, int port)
    {
        var parsed = RelayBridgeFinder.ParseOrAddress(raw);
        Assert.NotNull(parsed);
        Assert.Equal(host, parsed!.Value.address);
        Assert.Equal(port, parsed.Value.port);
    }

    [Theory]
    [InlineData("[2001:db8::1]:9001")]   // IPv6 — мимо
    [InlineData("no-port-here")]
    [InlineData(":-1")]
    [InlineData(":")]
    public void ParseOrAddress_Bad(string raw)
    {
        Assert.Null(RelayBridgeFinder.ParseOrAddress(raw));
    }

    [Fact]
    public void ParseRegistry_TakesIpv4Relays()
    {
        var json = """
        {"relays":[
          {"fingerprint":"AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111",
           "or_addresses":["5.6.7.8:9001","[2001:db8::2]:9001"]},
          {"fingerprint":"BBBB2222BBBB2222BBBB2222BBBB2222BBBB2222",
           "or_addresses":["[2001:db8::3]:9001"]},
          {"fingerprint":"CCCC3333CCCC3333CCCC3333CCCC3333CCCC3333",
           "or_addresses":["9.9.9.9:8443"]}
        ]}
        """;
        var relays = RelayBridgeFinder.ParseRegistry(json);
        Assert.Equal(2, relays.Count);
        Assert.Equal("5.6.7.8:9001 AAAA1111AAAA1111AAAA1111AAAA1111AAAA1111",
            relays[0].ToBridgeLine());
        Assert.Equal("9.9.9.9:8443 CCCC3333CCCC3333CCCC3333CCCC3333CCCC3333",
            relays[1].ToBridgeLine());
    }

    [Fact]
    public void ParseRegistry_BrokenJson_Empty()
    {
        Assert.Empty(RelayBridgeFinder.ParseRegistry("{oops"));
        Assert.Empty(RelayBridgeFinder.ParseRegistry("""{"relays":[]}"""));
    }
}
