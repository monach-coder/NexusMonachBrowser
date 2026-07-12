using System.Collections.ObjectModel;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class ExtensionsWindow : Window
{
    private sealed class ExtensionRow
    {
        public ExtensionRow(CoreWebView2BrowserExtension extension) => Extension = extension;
        public CoreWebView2BrowserExtension Extension { get; }
        public string Name => Extension.Name;
        public string Id => Extension.Id;
        public string State => Extension.IsEnabled ? "Включено" : "Выключено";
    }

    private readonly CoreWebView2Profile _profile;
    private readonly ObservableCollection<ExtensionRow> _rows = [];

    public ExtensionsWindow(CoreWebView2Profile profile)
    {
        _profile = profile;
        InitializeComponent();
        ExtensionsList.ItemsSource = _rows;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e) => await RefreshAsync();

    private async Task RefreshAsync()
    {
        _rows.Clear();
        foreach (var extension in await _profile.GetBrowserExtensionsAsync())
            _rows.Add(new ExtensionRow(extension));
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Выберите папку распакованного расширения с manifest.json",
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await ExtensionService.InstallAsync(_profile, dialog.FolderName);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            GlassDialogWindow.Show(this, "Не удалось установить расширение:\n\n" + ex.Message,
                "Расширения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Toggle_Click(object sender, RoutedEventArgs e)
    {
        if (ExtensionsList.SelectedItem is not ExtensionRow row) return;
        await row.Extension.EnableAsync(!row.Extension.IsEnabled);
        await RefreshAsync();
    }

    private async void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (ExtensionsList.SelectedItem is not ExtensionRow row) return;
        if (GlassDialogWindow.Show(this, $"Удалить расширение «{row.Name}»?", "Расширения",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            await ExtensionService.RemoveAsync(row.Extension);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            GlassDialogWindow.Show(this, "Не удалось удалить расширение:\n\n" + ex.Message,
                "Расширения", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
