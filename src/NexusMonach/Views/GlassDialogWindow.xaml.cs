using System.Windows;
using System.Windows.Media;

namespace NexusMonach.Views;

public partial class GlassDialogWindow : Window
{
    private readonly MessageBoxButton _buttons;
    private MessageBoxResult _result;

    private GlassDialogWindow(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        Title = string.IsNullOrWhiteSpace(title) ? "Nexus Monach" : title;
        MessageText.Text = message;
        _buttons = buttons;
        OkButton.Visibility = buttons == MessageBoxButton.OK ? Visibility.Visible : Visibility.Collapsed;
        YesButton.Visibility = buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? Visibility.Visible : Visibility.Collapsed;
        NoButton.Visibility = buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel ? Visibility.Visible : Visibility.Collapsed;
        CancelDialogButton.Visibility = buttons is MessageBoxButton.OKCancel or MessageBoxButton.YesNoCancel ? Visibility.Visible : Visibility.Collapsed;
        if (buttons == MessageBoxButton.OKCancel) OkButton.Visibility = Visibility.Visible;
        var icon = image switch
        {
            MessageBoxImage.Warning => (Text: "⚠", Brush: (Brush)Brushes.DarkOrange),
            MessageBoxImage.Error => (Text: "⛔", Brush: (Brush)Brushes.OrangeRed),
            MessageBoxImage.Question => (Text: "?", Brush: (Brush)FindResource("GoldBrush")),
            _ => (Text: "◆", Brush: (Brush)FindResource("AccentBrush"))
        };
        IconText.Text = icon.Text;
        IconText.Foreground = icon.Brush;
    }

    public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons, MessageBoxImage image) =>
        Show(null, message, title, buttons, image);

    public static MessageBoxResult Show(Window? owner, string message, string title,
        MessageBoxButton buttons, MessageBoxImage image)
    {
        var dialog = new GlassDialogWindow(message, title, buttons, image);
        if (owner?.IsVisible == true) dialog.Owner = owner;
        dialog.ShowDialog();
        if (dialog._result != MessageBoxResult.None) return dialog._result;
        return buttons is MessageBoxButton.YesNo or MessageBoxButton.YesNoCancel
            ? MessageBoxResult.No
            : buttons == MessageBoxButton.OK ? MessageBoxResult.OK : MessageBoxResult.Cancel;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) { _result = MessageBoxResult.OK; Close(); }
    private void Yes_Click(object sender, RoutedEventArgs e) { _result = MessageBoxResult.Yes; Close(); }
    private void No_Click(object sender, RoutedEventArgs e) { _result = MessageBoxResult.No; Close(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) { _result = MessageBoxResult.Cancel; Close(); }
}
