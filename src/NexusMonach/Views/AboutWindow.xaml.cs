using System.Windows;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var fabric = NexusFabricRuntime.Status;
        FabricStatusText.Text = fabric.IsAvailable
            ? $"{fabric.Product} {fabric.Version} · {string.Join(" · ", fabric.Features)}\n{fabric.Message}"
            : fabric.Message;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
