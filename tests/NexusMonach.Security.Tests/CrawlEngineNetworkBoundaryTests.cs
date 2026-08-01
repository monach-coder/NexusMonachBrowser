using System.Net;
using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class CrawlEngineNetworkBoundaryTests
{
    [Theory]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::1]/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://168.63.129.16/")]
    [InlineData("http://100.100.100.200/")]
    [InlineData("https://metadata.google.internal/")]
    [InlineData("https://service.local/")]
    [InlineData("https://192.168.1.1/")]
    [InlineData("https://example.com:8443/")]
    [InlineData("https://user@example.com/")]
    public void UnsafeCrawlerTargets_AreRejected(string url)
    {
        Assert.False(NexusSearchNetworkGuard.TryParsePublicHttpUri(url, out _));
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("https://1.1.1.1/")]
    public void PublicDefaultPortTargets_AreAccepted(string url)
    {
        Assert.True(NexusSearchNetworkGuard.TryParsePublicHttpUri(url, out _));
    }

    [Fact]
    public void MixedPublicAndPrivateDnsAnswers_AreRejectedAsRebinding()
    {
        IPAddress[] answers = [IPAddress.Parse("1.1.1.1"), IPAddress.Loopback];

        Assert.False(NexusSearchNetworkGuard.AreResolvedAddressesAllowed(answers));
    }

    [Fact]
    public void PublicDnsAnswers_AreAccepted()
    {
        IPAddress[] answers = [IPAddress.Parse("1.1.1.1"), IPAddress.Parse("8.8.8.8")];

        Assert.True(NexusSearchNetworkGuard.AreResolvedAddressesAllowed(answers));
    }

    [Fact]
    public void ExcessiveDnsAnswerSet_IsRejected()
    {
        var answers = Enumerable.Range(1, 17)
            .Select(index => IPAddress.Parse($"8.8.8.{index}"))
            .ToArray();

        Assert.False(NexusSearchNetworkGuard.AreResolvedAddressesAllowed(answers));
    }
}
