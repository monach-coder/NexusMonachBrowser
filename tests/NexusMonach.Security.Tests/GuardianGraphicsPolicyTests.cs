using Nexus.Guardian;

namespace NexusMonach.Security.Tests;

public sealed class GuardianGraphicsPolicyTests
{
    // Реальная сигнатура сбоя 24.08.2026: гибель потока рендеринга WPF.
    private const string RenderThreadStack =
        "System.Runtime.InteropServices.COMException (0x88980406): UCEERR_RENDERTHREADFAILURE (0x88980406)\r\n" +
        "   at System.Windows.Media.Composition.DUCE.Channel.SyncFlush()\r\n" +
        "   at System.Windows.Interop.HwndTarget.UpdateWindowSettings(Boolean enableRenderTarget, Nullable`1 channelSet)";

    [Fact]
    public void RenderThreadFailure_IsGraphicsFailure()
    {
        Assert.True(Program.IsGraphicsFailureReport(
            "wpf", "System.Runtime.InteropServices.COMException", RenderThreadStack));
    }

    [Fact]
    public void OutOfMemoryInCompositionChannel_IsGraphicsFailure()
    {
        Assert.True(Program.IsGraphicsFailureReport(
            "wpf", "System.OutOfMemoryException",
            "System.OutOfMemoryException: ...\r\n   at System.Windows.Media.Composition.DUCE.Channel.Flush()"));
    }

    [Fact]
    public void TransientCompositionDisabled_IsNotGraphicsFailure()
    {
        // 0x80263001 — перезапуск DWM: проходящее состояние, безопасный режим не нужен.
        Assert.False(Program.IsGraphicsFailureReport(
            "wpf", "System.Runtime.InteropServices.COMException",
            "System.Runtime.InteropServices.COMException (0x80263001)\r\n" +
            "   at Standard.NativeMethods.DwmExtendFrameIntoClientArea(IntPtr hwnd, MARGINS& pMarInset)"));
    }

    [Fact]
    public void XamlParseFailure_IsNotGraphicsFailure()
    {
        Assert.False(Program.IsGraphicsFailureReport(
            "wpf", "System.Windows.Markup.XamlParseException",
            "System.Exception: Не удается найти ресурс с именем \"SettingsCard\".\r\n" +
            "   at System.Windows.StaticResourceExtension.ProvideValueInternal(...)"));
    }

    [Theory]
    [InlineData("runtime")]
    [InlineData("tasks")]
    [InlineData(null)]
    [InlineData("")]
    public void NonWpfComponent_IsNeverGraphicsFailure(string? component)
    {
        Assert.False(Program.IsGraphicsFailureReport(
            component, "System.Runtime.InteropServices.COMException", RenderThreadStack));
    }

    [Fact]
    public void RenderThreadFailureOutsideCompositionChannel_IsIgnored()
    {
        Assert.False(Program.IsGraphicsFailureReport(
            "wpf", "System.Runtime.InteropServices.COMException",
            "System.Runtime.InteropServices.COMException (0x88980406): UCEERR_RENDERTHREADFAILURE (0x88980406)\r\n" +
            "   at Some.Unrelated.Place.Method()"));
    }
}
