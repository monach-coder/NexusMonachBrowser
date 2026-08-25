using System.Windows;
using System.Windows.Media;
using NexusMonach.Models;

namespace NexusMonach.Services;

public static class ThemeService
{
    private sealed record Palette(string Background, string Panel, string PanelHover,
        string Chrome, string Toolbar, string Field, string Border, string Overlay,
        string Selected, string Accent, string Gold, string Text, string Muted, string OnAccent);

    public static void Apply(BrowserTheme theme, BrowserThemeMode mode)
    {
        var accent = theme switch
        {
            BrowserTheme.Ocean => (Accent: "#2389D7", Gold: "#2B8499"),
            BrowserTheme.Forest => (Accent: "#238C58", Gold: "#9A7C21"),
            BrowserTheme.Amethyst => (Accent: "#8050C8", Gold: "#A97824"),
            _ => (Accent: "#168F83", Gold: "#9B741D")
        };

        var palette = mode == BrowserThemeMode.Light
            ? new Palette(
                "#F5F7FA", "#FFFFFF", "#E8EEF4", "#EEF2F6", "#FAFBFC", "#FFFFFF",
                "#C6D1DB", "#99FFFFFF", "#DDE8EF", accent.Accent, accent.Gold,
                "#17212B", "#5C6B78", "#FFFFFF")
            : theme switch
            {
                BrowserTheme.Ocean => Dark("#09111D", "#101D2D", "#182D43", "#4BB7FF", "#79D4E8", "#F1F7FC", "#91A8BC"),
                BrowserTheme.Forest => Dark("#09140F", "#102219", "#183326", "#50D890", "#D1BA69", "#EFF8F2", "#91AA99"),
                BrowserTheme.Amethyst => Dark("#100B18", "#1C1429", "#2A1D3D", "#B68CFF", "#E0B86F", "#F6F0FF", "#AA9AB9"),
                _ => Dark("#0B1018", "#121A26", "#1B2737", "#36D7C4", "#DAB96A", "#EEF4F8", "#91A2B4")
            };

        Set("BackgroundColor", "BackgroundBrush", palette.Background);
        Set("PanelColor", "PanelBrush", palette.Panel);
        Set("PanelHoverColor", "PanelHoverBrush", palette.PanelHover);
        Set("ChromeColor", "ChromeBrush", palette.Chrome);
        Set("ToolbarColor", "ToolbarBrush", palette.Toolbar);
        Set("FieldColor", "FieldBrush", palette.Field);
        Set("BorderColor", "BorderBrush", palette.Border);
        Set("OverlayColor", "OverlayBrush", palette.Overlay);
        Set("SelectedColor", "SelectedBrush", palette.Selected);
        Set("AccentColor", "AccentBrush", palette.Accent);
        Set("GoldColor", "GoldBrush", palette.Gold);
        Set("TextColor", "TextBrush", palette.Text);
        Set("MutedTextColor", "MutedTextBrush", palette.Muted);
        Set("OnAccentColor", "OnAccentBrush", palette.OnAccent);
    }

    public static void Apply(BrowserTheme theme) => Apply(theme, BrowserThemeMode.Dark);

    private static Palette Dark(string background, string panel, string panelHover,
        string accent, string gold, string text, string muted) => new(
        background, panel, panelHover, "#0A0F17", panel, "#0E1621", "#2C4056",
        "#99101010", panelHover, accent, gold, text, muted, "#07130F");

    private static void Set(string colorKey, string brushKey, string value)
    {
        var color = (Color)ColorConverter.ConvertFromString(value);
        Application.Current.Resources[colorKey] = color;
        if (Application.Current.Resources[brushKey] is SolidColorBrush brush && !brush.IsFrozen)
            brush.Color = color;
        else
            Application.Current.Resources[brushKey] = new SolidColorBrush(color);
    }
}
