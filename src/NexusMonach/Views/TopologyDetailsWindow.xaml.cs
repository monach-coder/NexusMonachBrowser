using System.Windows;

namespace NexusMonach.Views;

public partial class TopologyDetailsWindow : Window
{
    public TopologyDetailsWindow(string heading, string summary, string details)
    {
        InitializeComponent();
        Title = heading + " — Nexus Monach";
        HeadingText.Text = heading;
        SummaryText.Text = summary;
        DetailsTextBox.Text = details;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(DetailsTextBox.Text))
            Clipboard.SetText(DetailsTextBox.Text);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
