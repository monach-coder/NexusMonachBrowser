using System.Windows;
using System.Windows.Media;
using NexusMonach.Models;

namespace NexusMonach.Services;

public static class ThemeService
{
    private sealed record Palette(string Background, string Panel, string PanelHover,
        string Accent, string Gold, string Text, string Muted);

    public static void Apply(BrowserTheme theme)
    {
        var palette = theme switch
        {
            BrowserTheme.Ocean => new Palette(
                "#09111D", "#101D2D", "#182D43", "#4BB7FF", "#79D4E8", "#F1F7FC", "#91A8BC"),
            BrowserTheme.Forest => new Palette(
                "#09140F", "#102219", "#183326", "#50D890", "#D1BA69", "#EFF8F2", "#91AA99"),
            BrowserTheme.Amethyst => new Palette(
                "#100B18", "#1C1429", "#2A1D3D", "#B68CFF", "#E0B86F", "#F6F0FF", "#AA9AB9"),
            _ => new Palette(
                "#0B1018", "#121A26", "#1B2737", "#36D7C4", "#DAB96A", "#EEF4F8", "#91A2B4")
        };

        Set("BackgroundColor", "BackgroundBrush", palette.Background);
        Set("PanelColor", "PanelBrush", palette.Panel);
        Set("PanelHoverColor", "PanelHoverBrush", palette.PanelHover);
        Set("AccentColor", "AccentBrush", palette.Accent);
        Set("GoldColor", "GoldBrush", palette.Gold);
        Set("TextColor", "TextBrush", palette.Text);
        Set("MutedTextColor", "MutedTextBrush", palette.Muted);
    }

    private static void Set(string colorKey, string brushKey, string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        Application.Current.Resources[colorKey] = color;
        Application.Current.Resources[brushKey] = new SolidColorBrush(color);
    }
}
