using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using NexusMonach.Models;

namespace NexusMonach.Services;

/// <summary>
/// Applies native Windows 11 framing to borderless Nexus windows. WPF owns the
/// inside palette; DWM owns the real rounded corners, resize border and shadow.
/// </summary>
public static class WindowAppearanceService
{
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmWindowCornerRound = 2;

    public static void Apply(Window window, BrowserThemeMode mode)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        var dark = mode == BrowserThemeMode.Dark ? 1 : 0;
        var corner = DwmWindowCornerRound;
        _ = DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref dark, sizeof(int));
        _ = DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref corner, sizeof(int));

        var border = ResolveBorderColor();
        _ = DwmSetWindowAttribute(handle, DwmBorderColor, ref border, sizeof(uint));
    }

    private static uint ResolveBorderColor()
    {
        if (Application.Current.Resources["BorderBrush"] is not SolidColorBrush brush)
            return 0x00FFFFFF;

        var color = brush.Color;
        return (uint)(color.R | color.G << 8 | color.B << 16);
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int value, int valueSize);

    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttributeColor(IntPtr hwnd, int attribute,
        ref uint value, int valueSize);

    private static int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int valueSize) =>
        DwmSetWindowAttributeColor(hwnd, attribute, ref value, valueSize);
}
