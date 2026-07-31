using System.Windows;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = typeof(AboutWindow).Assembly.GetName().Version?.ToString(3) ?? "—";
        VersionText.Text = $"Версия {version} · Chromium / WebView2";
        var fabric = NexusFabricRuntime.Status;
        FabricStatusText.Text = fabric.IsAvailable
            ? $"{fabric.Product} {fabric.Version} · {string.Join(" · ", fabric.Features)}\n{fabric.Message}"
            : fabric.Message;
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
