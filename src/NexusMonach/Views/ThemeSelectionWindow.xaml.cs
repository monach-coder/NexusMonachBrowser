using System.Windows;
using NexusMonach.Models;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class ThemeSelectionWindow : Window
{
    public BrowserTheme ResultTheme { get; private set; }
    public BrowserThemeMode ResultMode { get; private set; }

    public ThemeSelectionWindow(BrowserTheme current, BrowserThemeMode mode)
    {
        InitializeComponent();
        ResultTheme = current;
        ResultMode = mode;
        LightModeChoice.IsChecked = mode == BrowserThemeMode.Light;
        DarkModeChoice.IsChecked = mode != BrowserThemeMode.Light;
        var choice = current switch
        {
            BrowserTheme.Ocean => OceanChoice,
            BrowserTheme.Forest => ForestChoice,
            BrowserTheme.Amethyst => AmethystChoice,
            _ => AquaChoice
        };
        choice.IsChecked = true;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        ResultTheme = OceanChoice.IsChecked == true ? BrowserTheme.Ocean :
            ForestChoice.IsChecked == true ? BrowserTheme.Forest :
            AmethystChoice.IsChecked == true ? BrowserTheme.Amethyst :
            BrowserTheme.MonachAqua;
        ResultMode = LightModeChoice.IsChecked == true
            ? BrowserThemeMode.Light
            : BrowserThemeMode.Dark;
        ThemeService.Apply(ResultTheme, ResultMode);
        DialogResult = true;
    }

    private void ModeChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ResultMode = LightModeChoice.IsChecked == true
            ? BrowserThemeMode.Light
            : BrowserThemeMode.Dark;
        ThemeService.Apply(ResultTheme, ResultMode);
    }

    private void ThemeChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        ResultTheme = sender is FrameworkElement { Tag: string value } &&
                      Enum.TryParse<BrowserTheme>(value, out var parsed)
            ? parsed
            : BrowserTheme.MonachAqua;
        ThemeService.Apply(ResultTheme, ResultMode);
    }
}
