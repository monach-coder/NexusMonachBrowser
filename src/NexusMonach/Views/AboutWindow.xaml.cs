using System.Windows;

namespace NexusMonach.Views;

public partial class AboutWindow : Window
{
    public AboutWindow() => InitializeComponent();
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
