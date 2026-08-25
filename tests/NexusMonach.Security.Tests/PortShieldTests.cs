using NexusMonach.Services;
using Xunit;

namespace NexusMonach.Security.Tests;

public sealed class PortShieldTests
{
    [Fact]
    public void AutoClosedLeaks_CoverOnlyNetworkDiscoveryLeaks()
    {
        // Никаких пользовательских служб (SMB/RDP/VNC/SSH) в списке авто-закрытия.
        var ports = PortShieldService.AutoClosedLeaks.Select(l => l.Port).ToHashSet();
        Assert.All(PortShieldService.AutoClosedLeaks, l =>
            Assert.Equal(l.Protocol, l.Protocol.ToUpperInvariant()));
        Assert.Contains(5353, ports);   // mDNS
        Assert.Contains(1900, ports);   // SSDP
        Assert.Contains(137, ports);    // NetBIOS
        Assert.DoesNotContain(445, ports);   // SMB — пользовательский сервис
        Assert.DoesNotContain(3389, ports);  // RDP — пользовательский сервис
        Assert.DoesNotContain(5900, ports);  // VNC — пользовательский сервис
    }

    [Fact]
    public void RuleNames_AreDeterministicAndPrefixed()
    {
        var leak = PortShieldService.AutoClosedLeaks.First(l => l.Port == 5353);
        var name = PortShieldService.RuleName(leak);
        Assert.StartsWith("Nexus Leak Guard", name, StringComparison.Ordinal);
        Assert.Contains("UDP 5353", name);
        Assert.Equal(name, PortShieldService.RuleName(leak));
    }

    [Fact]
    public void BuildRuleScript_Add_CreatesInOutBlocksAndIsIdempotent()
    {
        var leaks = PortShieldService.AutoClosedLeaks.Take(2).ToList();
        var script = PortShieldService.BuildRuleScript(leaks, add: true);

        foreach (var leak in leaks)
        {
            Assert.Contains($"Remove-NetFirewallRule -DisplayName '{PortShieldService.RuleName(leak)}'", script);
            Assert.Contains($"-LocalPort {leak.Port}", script);
            Assert.Contains("-Direction Inbound -Action Block", script);
            Assert.Contains("-Direction Outbound -Action Block", script);
        }
    }

    [Fact]
    public void BuildRuleScript_Remove_OnlyDeletes()
    {
        var script = PortShieldService.BuildRuleScript(
            PortShieldService.AutoClosedLeaks.ToList(), add: false);
        Assert.DoesNotContain("New-NetFirewallRule", script);
        Assert.Contains("Remove-NetFirewallRule", script);
        Assert.Equal(
            PortShieldService.AutoClosedLeaks.Length,
            CountOccurrences(script, "Remove-NetFirewallRule"));
    }

    private static int CountOccurrences(string text, string token)
    {
        var count = 0;
        for (var index = 0; (index = text.IndexOf(token, index, StringComparison.Ordinal)) >= 0; index += token.Length)
            count++;
        return count;
    }
}
