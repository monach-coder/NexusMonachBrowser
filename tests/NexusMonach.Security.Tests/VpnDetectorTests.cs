using NexusMonach.Services.Tor;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Распознавание VPN-адаптера: системные переходные псевдоинтерфейсы Windows
/// (Teredo, ISATAP, 6to4) всегда «Up» и имеют тип Tunnel — VPN они не
/// являются. Ложное «VPN активен» заворачивало вкладки в необёрнутый
/// анонимный слой, который без туннеля не выходит в сеть (реальный случай
/// на Teredo).
/// </summary>
public class VpnDetectorTests
{
    [Theory]
    [InlineData("Tunnel", "Microsoft Teredo Tunneling Adapter", "Teredo Tunneling Pseudo-Interface 1", false)]
    [InlineData("Tunnel", "Microsoft ISATAP Adapter", "isatap.localdomain", false)]
    [InlineData("Tunnel", "Microsoft 6to4 Adapter", "6to4 Adapter", false)]
    [InlineData("Ethernet", "Software Loopback Interface 1", "Loopback Pseudo-Interface 1", false)]
    public void TransitionAndPseudoInterfaces_AreNotVpn(
        string type, string description, string name, bool expected)
    {
        Assert.Equal(expected, VpnDetector.IsVpnAdapter(type, description, name));
    }

    [Theory]
    [InlineData("Ppp", "WAN Miniport", "AdGuard VPN", true)]
    [InlineData("Tunnel", "WireGuard Tunnel", "wg0", true)]
    [InlineData("Ethernet", "TAP-Windows Adapter V9", "Локальная сеть", true)]
    [InlineData("Ethernet", "OpenVPN TAP", "VPN-соединение", true)]
    public void RealVpnAdapters_AreDetected(
        string type, string description, string name, bool expected)
    {
        Assert.Equal(expected, VpnDetector.IsVpnAdapter(type, description, name));
    }

    [Theory]
    [InlineData("Ethernet", "Intel(R) Wi-Fi 6 AX201", "Беспроводная сеть")]
    [InlineData("Ethernet", "Hyper-V Virtual Ethernet Adapter", "vEthernet (WSLCore)")]
    [InlineData("Ethernet", "VirtualBox Host-Only Ethernet Adapter", "Ethernet 2")]
    public void OrdinaryAndVirtualAdapters_AreNotVpn(
        string type, string description, string name)
    {
        Assert.False(VpnDetector.IsVpnAdapter(type, description, name));
    }
}
