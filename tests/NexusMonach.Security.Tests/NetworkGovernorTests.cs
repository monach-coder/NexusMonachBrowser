using NexusMonach.Services;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Решающая функция управляющего выходом в сеть: чистая лестница действий
/// по живости прямого пути, WARP и настроенного сервера.
/// </summary>
public class NetworkGovernorTests
{
    [Fact]
    public void DirectAlive_Quiet()
    {
        Assert.Equal(GovernorStep.EgressOk, NetworkGovernor.Decide(
            directOk: true, warpConnected: false, warpInstalled: false,
            vlessConfigured: false, vlessRunning: false));
    }

    [Fact]
    public void SystemTunnelAlive_Quiet()
    {
        // Прямой путь мёртв, но системный туннель (WARP) несёт выход — покой.
        Assert.Equal(GovernorStep.EgressOk, NetworkGovernor.Decide(
            directOk: false, warpConnected: true, warpInstalled: true,
            vlessConfigured: false, vlessRunning: false));
    }

    [Fact]
    public void ServerAlreadyRunning_Quiet()
    {
        Assert.Equal(GovernorStep.EgressOk, NetworkGovernor.Decide(
            directOk: false, warpConnected: false, warpInstalled: false,
            vlessConfigured: true, vlessRunning: true));
    }

    [Fact]
    public void DirectDeadWithProfile_StartsServer()
    {
        // Настроенный сервер поднимается сам — до подсказок про WARP.
        Assert.Equal(GovernorStep.StartServer, NetworkGovernor.Decide(
            directOk: false, warpConnected: false, warpInstalled: true,
            vlessConfigured: true, vlessRunning: false));
    }

    [Fact]
    public void DirectDeadWarpOnly_Suggests()
    {
        Assert.Equal(GovernorStep.SuggestWarp, NetworkGovernor.Decide(
            directOk: false, warpConnected: false, warpInstalled: true,
            vlessConfigured: false, vlessRunning: false));
    }

    [Fact]
    public void NothingAvailable_HonestNoEgress()
    {
        Assert.Equal(GovernorStep.NoEgress, NetworkGovernor.Decide(
            directOk: false, warpConnected: false, warpInstalled: false,
            vlessConfigured: false, vlessRunning: false));
    }
}
