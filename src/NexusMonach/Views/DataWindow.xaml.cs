using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class DataWindow : Window
{
    private enum DataMode { Bookmarks, Downloads }

    private sealed class Row
    {
        public string Primary { get; init; } = string.Empty;
        public string Secondary { get; init; } = string.Empty;
        public string Value { get; init; } = string.Empty;
    }

    private readonly DataMode _mode;
    private readonly Func<string, Task>? _openUrl;
    private readonly ObservableCollection<Row> _rows = [];

    private DataWindow(DataMode mode, Func<string, Task>? openUrl)
    {
        _mode = mode;
        _openUrl = openUrl;
        InitializeComponent();
        ItemsList.ItemsSource = _rows;
        RefreshRows();
    }

    public static DataWindow ForBookmarks(Func<string, Task> openUrl) => new(DataMode.Bookmarks, openUrl);
    public static DataWindow ForDownloads() => new(DataMode.Downloads, null);

    private void RefreshRows()
    {
        _rows.Clear();
        switch (_mode)
        {
            case DataMode.Bookmarks:
                HeaderText.Text = "Закладки";
                ActionButton.Content = "Удалить";
                foreach (var item in BookmarkService.Items)
                    _rows.Add(new Row { Primary = item.Title, Secondary = item.Url, Value = item.Url });
                break;
            case DataMode.Downloads:
                HeaderText.Text = "Загрузки";
                OpenButton.Content = "Открыть файл";
                ActionButton.Content = "Показать в папке";
                foreach (var item in DownloadService.Items)
                    _rows.Add(new Row
                    {
                        Primary = item.FileName,
                        Secondary = item.ProgressText + "  ·  " + item.SecuritySummary +
                                    (string.IsNullOrWhiteSpace(item.Sha256) ? string.Empty : "  ·  SHA-256: " + item.Sha256),
                        Value = item.FilePath
                    });
                break;
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await OpenSelectedAsync();
    private async void ItemsList_MouseDoubleClick(object sender, MouseButtonEventArgs e) => await OpenSelectedAsync();

    private async Task OpenSelectedAsync()
    {
        if (ItemsList.SelectedItem is not Row row) return;
        if (_mode == DataMode.Downloads)
        {
            if (File.Exists(row.Value))
                Process.Start(new ProcessStartInfo(row.Value) { UseShellExecute = true });
            return;
        }

        if (_openUrl is not null)
            await _openUrl(row.Value);
        Close();
    }

    private async void Action_Click(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case DataMode.Bookmarks when ItemsList.SelectedItem is Row row:
                await BookmarkService.RemoveAsync(row.Value);
                RefreshRows();
                break;
            case DataMode.Downloads when ItemsList.SelectedItem is Row download:
                var directory = Path.GetDirectoryName(download.Value);
                if (directory is not null && Directory.Exists(directory))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{download.Value}\"") { UseShellExecute = true });
                break;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
