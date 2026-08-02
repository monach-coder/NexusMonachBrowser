using System.Windows;
using System.Windows.Controls;
using NexusMonach.Models;

namespace NexusMonach.Views;

public partial class SiteExplorerWindow : Window
{
    private readonly BrowserTab _tab;
    private SiteTreeItem? _selected;
    private bool _refreshing;

    public SiteExplorerWindow(BrowserTab tab)
    {
        _tab = tab;
        InitializeComponent();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        RefreshButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        StatusText.Text = "Снимаем очищенную структуру текущей страницы…";
        try
        {
            var snapshot = await _tab.CaptureSiteTreeAsync();
            OriginText.Text = string.IsNullOrWhiteSpace(snapshot.Origin) ? _tab.CurrentHost : snapshot.Origin;
            SummaryText.Text =
                $"{snapshot.PageTitle}\nЭлементов на странице: {snapshot.TotalElements}; " +
                $"показано DOM-узлов: {Count(snapshot.Structure)}; ссылок: {snapshot.Links.Count}; " +
                $"ресурсов: {snapshot.Resources.Count}. " +
                "Значения форм, cookies, storage, script/style и query/fragment не включаются.";

            var roots = new List<SiteTreeItem>
            {
                Folder($"Структура DOM · {Count(snapshot.Structure)}", snapshot.Structure,
                    "Очищенное дерево элементов. Копируется безопасный CSS-селектор, а не HTML страницы."),
                Folder($"Ссылки · {snapshot.Links.Count}", snapshot.Links,
                    "URL показаны без userinfo, query и fragment."),
                Folder($"Загруженные ресурсы · {snapshot.Resources.Count}", snapshot.Resources,
                    "Локальный список Performance API без параметров запросов.")
            };
            SiteTree.ItemsSource = roots;
            SelectionTitle.Text = "Выберите узел дерева";
            DetailsTextBox.Text = string.Empty;
            _selected = null;
            StatusText.Text = snapshot.Truncated
                ? "Снимок ограничен по размеру — крупная страница показана частично."
                : "Готово · снимок существует только в памяти этого окна.";
        }
        catch (Exception ex)
        {
            SiteTree.ItemsSource = null;
            StatusText.Text = "Структура недоступна: " + ex.Message;
        }
        finally
        {
            _refreshing = false;
            RefreshButton.IsEnabled = true;
        }
    }

    private static SiteTreeItem Folder(string title, List<SiteTreeItem> children, string details) => new()
    {
        Title = title,
        Details = details,
        Children = children
    };

    private static int Count(IEnumerable<SiteTreeItem> items) =>
        items.Sum(item => 1 + Count(item.Children));

    private void SiteTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        _selected = e.NewValue as SiteTreeItem;
        SelectionTitle.Text = _selected?.Title ?? "Выберите узел дерева";
        DetailsTextBox.Text = _selected?.Details ?? string.Empty;
        CopyButton.IsEnabled = _selected is not null &&
                               (!string.IsNullOrWhiteSpace(_selected.CopyValue) ||
                                !string.IsNullOrWhiteSpace(_selected.Details));
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        var value = string.IsNullOrWhiteSpace(_selected.CopyValue)
            ? _selected.Details
            : _selected.CopyValue;
        if (!string.IsNullOrWhiteSpace(value)) Clipboard.SetText(value);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
