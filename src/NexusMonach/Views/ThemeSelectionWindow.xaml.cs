using System.Windows;
using NexusMonach.Models;

namespace NexusMonach.Views;

public partial class ThemeSelectionWindow : Window
{
    public BrowserTheme ResultTheme { get; private set; }

    public ThemeSelectionWindow(BrowserTheme current)
    {
        InitializeComponent();
        ResultTheme = current;
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
        DialogResult = true;
    }
}
