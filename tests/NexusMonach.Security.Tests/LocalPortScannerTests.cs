using NexusMonach.Services;
using Xunit;

namespace NexusMonach.Security.Tests;

public class LocalPortScannerTests
{
    [Theory]
    [InlineData("TCP", 3389, "0.0.0.0", "svchost", 2, "RDP")]
    [InlineData("TCP", 445, "0.0.0.0", "System", 2, "SMB")]
    [InlineData("TCP", 5900, "192.168.1.5", "vncserver", 2, "VNC")]
    [InlineData("TCP", 22, "0.0.0.0", "sshd", 2, "Telnet/SSH")]
    [InlineData("UDP", 5353, "0.0.0.0", "svchost", 1, "mDNS")]
    [InlineData("TCP", 1900, "0.0.0.0", "svchost", 1, "SSDP/UPnP")]
    [InlineData("TCP", 53, "127.0.0.1", "AdGuard", 1, "DNS")]
    [InlineData("TCP", 137, "0.0.0.0", "System", 1, "NetBIOS")]
    [InlineData("TCP", 443, "127.0.0.1", "nginx", 0, "Открыт")]
    public void Classify_AssessesWellKnownPorts(
        string protocol, int port, string address, string process, int expectedSeverity, string expectedRisk)
    {
        var entry = LocalPortScanner.Classify(
            new LocalPortInfo(protocol, address, port, 1234, process));
        Assert.Equal(expectedSeverity, entry.Severity);
        Assert.Equal(expectedRisk, entry.Risk);
    }

    [Fact]
    public void Classify_WildcardBindingOfUnknownPort_IsExposedToNetwork()
    {
        var entry = LocalPortScanner.Classify(
            new LocalPortInfo("TCP", "0.0.0.0", 54321, 777, "myapp"));
        Assert.Equal(1, entry.Severity);
        Assert.Equal("Доступен из сети", entry.Risk);
    }

    [Fact]
    public void Classify_LoopbackUnknownPort_IsNeutral()
    {
        var entry = LocalPortScanner.Classify(
            new LocalPortInfo("TCP", "127.0.0.1", 54321, 777, "myapp"));
        Assert.Equal(0, entry.Severity);
    }

    [Fact]
    public void Classify_OwnHoneypotPort_IsFriendlyTrap()
    {
        // Порты ловушек случайны на сессию: сканер портов узнаёт их
        // из реестра Дозора, а не из статичного списка.
        var previous = NexusMonach.Services.Tor.NetworkWatchdog.ActiveSessionPorts;
        NexusMonach.Services.Tor.NetworkWatchdog.ActiveSessionPorts = [6379, 2222, 27017];
        try
        {
            var entry = LocalPortScanner.Classify(
                new LocalPortInfo("TCP", "0.0.0.0", 6379, 42, "NexusMonach.Browser"));
            Assert.Equal(0, entry.Severity);
            Assert.Equal("Ловушка Дозора", entry.Risk);
        }
        finally
        {
            NexusMonach.Services.Tor.NetworkWatchdog.ActiveSessionPorts = previous;
        }
    }

    [Fact]
    public void Classify_OwnPortOutsideSessionSet_IsNotFriendlyTrap()
    {
        var previous = NexusMonach.Services.Tor.NetworkWatchdog.ActiveSessionPorts;
        NexusMonach.Services.Tor.NetworkWatchdog.ActiveSessionPorts = [2222];
        try
        {
            var entry = LocalPortScanner.Classify(
                new LocalPortInfo("TCP", "0.0.0.0", 6379, 42, "NexusMonach.Browser"));
            Assert.NotEqual("Ловушка Дозора", entry.Risk);
        }
        finally
        {
            NexusMonach.Services.Tor.NetworkWatchdog.ActiveSessionPorts = previous;
        }
    }

    [Fact]
    public void Classify_ForeignProcessOnHoneypotPort_IsNotFriendlyTrap()
    {
        var entry = LocalPortScanner.Classify(
            new LocalPortInfo("TCP", "0.0.0.0", 6379, 42, "redis-server"));
        Assert.NotEqual("Ловушка Дозора", entry.Risk);
    }

    [Fact]
    public void Classify_TrailSocksPort_IsFriendly()
    {
        var entry = LocalPortScanner.Classify(
            new LocalPortInfo("TCP", "127.0.0.1", 9051, 7, "tor"));
        Assert.Equal(0, entry.Severity);
        Assert.Equal("След", entry.Risk);
    }

    [Fact]
    public void Classify_DangerousPortOnWildcard_NotesNetworkExposure()
    {
        var entry = LocalPortScanner.Classify(
            new LocalPortInfo("TCP", "0.0.0.0", 3389, 4, "svchost"));
        Assert.Equal(2, entry.Severity);
        Assert.Contains("доступен из сети", entry.Note);
    }

    [Fact]
    public void Scan_OnWindows_ReturnsLiveListeners()
    {
        if (!OperatingSystem.IsWindows()) return;
        var entries = LocalPortScanner.Scan();
        Assert.NotEmpty(entries);
        // Каждая запись обязана знать протокол, порт и владельца.
        Assert.All(entries, e =>
        {
            Assert.True(e.Port > 0 && e.Port <= 65535);
            Assert.False(string.IsNullOrWhiteSpace(e.ProcessName));
        });
    }
}
