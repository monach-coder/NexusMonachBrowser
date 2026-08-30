using NexusMonach.Services.Warp;
using Xunit;

namespace NexusMonach.Security.Tests;

/// <summary>
/// Классификация состояния WARP по адаптерам машины: чистая функция без
/// обращений к сети и процессам.
/// </summary>
public class WarpServiceTests
{
    [Fact]
    public void NoAdapter_NotInstalled()
    {
        Assert.Equal(WarpStatus.NotInstalled,
            WarpService.ClassifyAdapter(Array.Empty<(bool, bool)>()));
    }

    [Fact]
    public void WarpAdapterUp_Connected()
    {
        Assert.Equal(WarpStatus.Connected,
            WarpService.ClassifyAdapter(new[] { (false, true), (true, true) }));
    }

    [Fact]
    public void WarpAdapterDown_Disconnected()
    {
        Assert.Equal(WarpStatus.Disconnected,
            WarpService.ClassifyAdapter(new[] { (false, true) }));
    }

    [Fact]
    public void OtherAdapters_DontCount()
    {
        Assert.Equal(WarpStatus.NotInstalled,
            WarpService.ClassifyAdapter(new[] { (true, false), (true, false) }));
    }
}
