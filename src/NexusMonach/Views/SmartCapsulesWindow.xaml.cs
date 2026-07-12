using System.Windows;
using System.Windows.Controls;
using NexusMonach.Models;

namespace NexusMonach.Views;

public partial class SmartCapsulesWindow : Window
{
    private readonly Func<SmartCapsule, Task> _archive;
    private SmartCapsule? Selected => CapsulesList.SelectedItem as SmartCapsule;

    public SmartCapsulesWindow(IReadOnlyList<SmartCapsule> capsules, Func<SmartCapsule, Task> archive)
    {
        _archive = archive;
        InitializeComponent();
        CapsulesList.ItemsSource = capsules;
        if (capsules.Count > 0) CapsulesList.SelectedIndex = 0;
    }

    private void CapsulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var capsule = Selected;
        ArchiveButton.IsEnabled = capsule is not null;
        CapsuleNameText.Text = capsule?.Name ?? "Выбери капсулу";
        CapsuleSummaryText.Text = capsule?.Summary ?? string.Empty;
        CapsuleItemsText.Text = capsule?.ItemsText ?? string.Empty;
    }

    private async void Archive_Click(object sender, RoutedEventArgs e)
    {
        var capsule = Selected;
        if (capsule is null) return;
        if (GlassDialogWindow.Show(this,
                $"Сохранить «{capsule.Name}» в граф знаний и закрыть {capsule.Urls.Count} вкладок?",
                "Smart Capsule", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        ArchiveButton.IsEnabled = false;
        await _archive(capsule);
        Close();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
