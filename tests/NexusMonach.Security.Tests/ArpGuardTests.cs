using NexusMonach.Services.Tor;
using Xunit;

namespace NexusMonach.Security.Tests;

public sealed class ArpGuardTests
{
    [Fact]
    public void ReadArpTable_ReturnsRealKernelRows()
    {
        var rows = ArpGuard.ReadArpTable();
        // Записи с MAC существуют (обычные соседи по сети); их MAC — 12 hex.
        // Multicast-строки без MAC ядро тоже отдаёт — это норма.
        Assert.Contains(rows, r => r.Mac.Length == 12);
        Assert.All(rows.Where(r => r.Mac.Length > 0),
            r => Assert.Equal(12, r.Mac.Length));
    }

    [Fact]
    public void FormatMac_AddsDashes()
    {
        Assert.Equal("AA-BB-CC-DD-EE-FF", ArpGuard.FormatMac("AABBCCDDEEFF"));
    }

    [Theory]
    [InlineData(0x0100A8C0, "192.168.0.1")]
    [InlineData(0xFFFFFF7F, "127.255.255.255")]
    public void IntToIp_ConvertsLittleEndian(uint value, string expected)
    {
        Assert.Equal(expected, ArpGuard.IntToIp(value));
    }

    [Fact]
    public void BuildPinScript_PinsBaselineMacPermanently()
    {
        var script = ArpGuard.BuildPinScript("Ethernet", "192.168.1.1", "AA-BB-CC-DD-EE-FF");
        Assert.Contains("New-NetNeighbor -InterfaceAlias 'Ethernet' -IPAddress 192.168.1.1", script);
        Assert.Contains("-LinkLayerAddress 'AA-BB-CC-DD-EE-FF'", script);
        Assert.Contains("-State Permanent", script);
        // Идемпотентность: старая запись снимается перед закреплением.
        Assert.Contains("Remove-NetNeighbor", script);
    }

    [Fact]
    public void BuildEvidence_ContainsBothMacsAndSnapshot()
    {
        var rows = new List<ArpRow>
        {
            new(12, 0x0100A8C0, "AABBCCDDEEFF", 3),
            new(12, 0x0200A8C0, "112233445566", 3)
        };
        var evidence = ArpGuard.BuildEvidence(
            "192.168.0.1", "Ethernet", "AABBCCDDEEFF", "112233445566", rows);
        Assert.Contains("AA-BB-CC-DD-EE-FF", evidence);
        Assert.Contains("11-22-33-44-55-66", evidence);
        Assert.Contains("192.168.0.2 → 11-22-33-44-55-66", evidence);
        Assert.Contains("Ethernet", evidence);
    }
}
