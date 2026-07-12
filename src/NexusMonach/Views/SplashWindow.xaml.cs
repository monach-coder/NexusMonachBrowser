using System.Windows;
using System.Windows.Media.Animation;

namespace NexusMonach.Views;

public partial class SplashWindow : Window
{
    public SplashWindow() => InitializeComponent();

    private void Grid_Loaded(object sender, RoutedEventArgs e) =>
        ((Storyboard)FindResource("Pulse")).Begin(this, true);
}
