using System.IO;
using System.Windows;
using NexusMonach.Services.Tor;

namespace NexusMonach.Views;

/// <summary>
/// Капча Moat: человек решает задание разда́тчика Tor Project, браузер
/// отправляет решение и получает приватные webtunnel-мосты.
/// </summary>
public partial class MoatCaptchaWindow : Window
{
    private readonly MoatBridgeFetcher.Challenge _challenge;

    public IReadOnlyList<string> Bridges { get; private set; } = Array.Empty<string>();

    public MoatCaptchaWindow(MoatBridgeFetcher.Challenge challenge)
    {
        InitializeComponent();
        _challenge = challenge;
        using var stream = new MemoryStream(challenge.ImagePng);
        var bitmap = new System.Windows.Media.Imaging.BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bitmap.StreamSource = stream;
        bitmap.EndInit();
        bitmap.Freeze();
        CaptchaImage.Source = bitmap;
        SolutionBox.Focus();
    }

    private async void Accept_Click(object sender, RoutedEventArgs e)
    {
        var solution = SolutionBox.Text.Trim();
        if (solution.Length == 0) return;
        IsEnabled = false;
        try
        {
            Bridges = await MoatBridgeFetcher.CheckAsync(_challenge, solution);
            DialogResult = Bridges.Count > 0;
        }
        finally
        {
            IsEnabled = true;
        }
    }
}
