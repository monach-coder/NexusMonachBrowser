using NexusMonach.Services.Tor;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Ротация приватных мостов и разбор ответов Moat-разда́тчика Tor Project:
/// чистые функции, сеть в тестах не участвует.
/// </summary>
public class BridgeRotationTests
{
    [Fact]
    public void PickSessionBridge_EmptyPool_Null()
    {
        Assert.Null(BridgeRotator.PickSessionBridge(""));
        Assert.Null(BridgeRotator.PickSessionBridge(null));
        Assert.Null(BridgeRotator.PickSessionBridge("# только комментарий\n\n"));
    }

    [Fact]
    public void PickSessionBridge_ReturnsLineFromPool()
    {
        var pool = "webtunnel 1.1.1.1:443 AAAA url=https://a/b cert=xx\n" +
                   "# комментарий\n" +
                   "webtunnel 2.2.2.2:8443 BBBB url=https://c/d cert=yy";
        for (var i = 0; i < 20; i++)
        {
            var pick = BridgeRotator.PickSessionBridge(pool, count => count - 1);
            Assert.Contains("webtunnel", pick, StringComparison.Ordinal);
            Assert.DoesNotContain("#", pick, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PickSessionBridge_RotatesAcrossSessions()
    {
        var pool = string.Join("\n", Enumerable.Range(0, 8).Select(i => $"webtunnel 10.0.0.{i}:443 FP{i}"));
        var picked = new System.Collections.Generic.HashSet<string>();
        for (var i = 0; i < 100; i++)
            picked.Add(BridgeRotator.PickSessionBridge(pool)!);
        // Криптослучайность: за 100 сессий должно побывать больше одного моста.
        Assert.True(picked.Count > 1, "ротация не меняет мост между сессиями");
    }

    [Fact]
    public void MoatParse_Challenge()
    {
        var json = """
        {"data":[{"type":"moat-challenge","transport":"webtunnel",
        "image":"data:image/jpeg;base64,QUJD","id":"42","moat_version":"0.1.0"}]}
        """;
        var challenge = MoatBridgeFetcher.ParseChallenge(json);
        Assert.NotNull(challenge);
        Assert.Equal("42", challenge!.Id);
        Assert.Equal("webtunnel", challenge.Transport);
        Assert.Equal(new byte[] { 65, 66, 67 }, challenge.ImagePng);
    }

    [Fact]
    public void MoatParse_Bridges()
    {
        var json = """
        {"data":[{"type":"moat-bridges",
        "bridges":["webtunnel 1.2.3.4:443 FP url=https://a/b cert=x",
                   "webtunnel 5.6.7.8:443 FP2 url=https://c/d cert=y"]}]}
        """;
        var bridges = MoatBridgeFetcher.ParseBridges(json);
        Assert.Equal(2, bridges.Count);
        Assert.StartsWith("webtunnel", bridges[0], StringComparison.Ordinal);
    }

    [Fact]
    public void MoatParse_BrokenJson_Empty()
    {
        Assert.Null(MoatBridgeFetcher.ParseChallenge("{oops"));
        Assert.Empty(MoatBridgeFetcher.ParseBridges("{oops"));
    }
}
