using System.Windows;

namespace NexusMonach.Views;

/// <summary>
/// Ввод публичного ключа собеседника для создания инвайта.
/// </summary>
public partial class InputForPublicKeyWindow : Window
{
    public byte[]? PublicKeyBytes { get; private set; }

    public InputForPublicKeyWindow()
    {
        InitializeComponent();
    }

    private void Accept_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var key = Convert.FromBase64String(KeyBox.Text.Trim());
            if (key.Length != 64)
            {
                GlassDialogWindow.Show(this, "Ключ должен быть 64 байта в base64 (получено " + key.Length + ").",
                    "Ключ собеседника", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            PublicKeyBytes = key;
            DialogResult = true;
        }
        catch (FormatException)
        {
            GlassDialogWindow.Show(this, "Это не base64 — проверьте копирование ключа.",
                "Ключ собеседника", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
