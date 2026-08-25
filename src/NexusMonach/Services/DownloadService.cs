using System.Collections.ObjectModel;
using NexusMonach.Models;

namespace NexusMonach.Services;

public static class DownloadService
{
    public static ObservableCollection<DownloadItem> Items { get; } = [];

    public static void Add(DownloadItem item)
    {
        Ui.Invoke(() =>
        {
            Items.Insert(0, item);
            while (Items.Count > 100)
                Items.RemoveAt(Items.Count - 1);
        });
    }
}
