using NexusMonach.Services;

namespace NexusMonach.Security.Tests;

public sealed class WebView2RuntimeMonitorTests
{
    private static WebView2RuntimeSnapshot Snapshot(WebView2RuntimeState state,
        string active = "150.0.4078.100", string installed = "150.0.4078.105") =>
        new(state, active, installed, "1.0.4078.44", DateTimeOffset.Now, "status");

    [Fact]
    public void RestartPrompt_RequiresFreshMatchingLocalConfirmation()
    {
        var observed = Snapshot(WebView2RuntimeState.RestartRequired);

        Assert.True(WebView2RuntimeMonitor.ShouldOfferRestart(
            observed, Snapshot(WebView2RuntimeState.RestartRequired)));
        Assert.False(WebView2RuntimeMonitor.ShouldOfferRestart(
            observed, Snapshot(WebView2RuntimeState.Current)));
        Assert.True(WebView2RuntimeMonitor.ShouldOfferRestart(
            observed, Snapshot(WebView2RuntimeState.RestartRequired,
                installed: "150.0.4078.106")));
    }

    [Fact]
    public void StartupFailure_DoesNotSuggestReinstallWhenRuntimeIsPresent()
    {
        var message = WebView2RuntimeMonitor.FormatStartupFailure(
            new InvalidOperationException("test failure"),
            Snapshot(WebView2RuntimeState.Current));

        Assert.Contains("уже установлен", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Повторная установка компонента не требуется", message,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Установите официальный", message,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StartupFailure_SuggestsInstallOnlyWhenLocalRuntimeIsMissing()
    {
        var message = WebView2RuntimeMonitor.FormatStartupFailure(
            new InvalidOperationException("test failure"),
            Snapshot(WebView2RuntimeState.Missing, installed: "не найдено"));

        Assert.Contains("Установите официальный Evergreen Runtime", message,
            StringComparison.OrdinalIgnoreCase);
    }
}
