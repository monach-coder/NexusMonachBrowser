using NexusMonach.Services.Tor;
using Xunit;

namespace NexusMonach.Security.Tests;

public sealed class TorRelayTests
{
    [Fact]
    public void RelayLines_AlwaysIncludeExitReject_AndBridgeFlag()
    {
        var lines = TorRelayService.BuildRelayLines(true, "NexusMonach_test", 9101, 9102);

        Assert.Contains("ORPort 9101", lines);
        Assert.Contains("BridgeRelay 1", lines);
        // Мост — никогда exit-узел: выходной трафик запрещён абсолютно.
        Assert.Contains("ExitPolicy reject *:*", lines);
        Assert.Contains("Nickname NexusMonach_test", lines);
    }

    [Fact]
    public void RelayLines_Disabled_ReturnsEmpty()
    {
        Assert.Empty(TorRelayService.BuildRelayLines(false, "x", 9101, 9102));
    }

    [Theory]
    [InlineData("Nexus Monach! bridge", "NexusMonachbridge")]
    [InlineData("кириллица-ник", "")]
    [InlineData("  ", "")]
    [InlineData("A_b-1", "A_b1")]
    [InlineData("СлишкомДлинноеИмяБраузераБольшеДевятнадцати", "NexusMonach")]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ", "ABCDEFGHIJKLMNOPQRS")]
    public void Nickname_SanitizedToTorAlphabet(string input, string expected)
    {
        Assert.Equal(expected == "" ? "NexusMonach" : expected,
            TorRelayService.SanitizeNickname(input));
    }

    [Fact]
    public void DefaultNickname_IsStableShape()
    {
        var nick = TorRelayService.DefaultNickname();
        Assert.StartsWith("NexusMonach-", nick, StringComparison.Ordinal);
        Assert.Matches("^NexusMonach-[0-9a-f]{4}$", nick);
        Assert.True(nick.Length <= 19);
    }

    [Fact]
    public void Torrc_IncludesRelaySection_WhenEnabled()
    {
        var torrc = TorBridgeManager.GenerateTorrc(
            bridges: [], socksPort: 9051,
            relayEnabled: true, relayNickname: "Bridge1", relayOrPort: 9101, relayObfs4Port: 9102);
        Assert.Contains("ORPort 9101", torrc, StringComparison.Ordinal);
        Assert.Contains("ExitPolicy reject *:*", torrc, StringComparison.Ordinal);
        Assert.Contains("SocksPort 127.0.0.1:9051", torrc, StringComparison.Ordinal);
    }

    [Fact]
    public void Torrc_NoRelaySection_WhenDisabled()
    {
        var torrc = TorBridgeManager.GenerateTorrc(
            bridges: [], socksPort: 9051, relayEnabled: false);
        Assert.DoesNotContain("ORPort", torrc, StringComparison.Ordinal);
        Assert.DoesNotContain("BridgeRelay", torrc, StringComparison.Ordinal);
    }
}
