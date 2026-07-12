using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NexusMonach.Models;

namespace NexusMonach.Views;

public partial class KnowledgeGraphWindow : Window
{
    private readonly KnowledgeGraphData _graph;
    private readonly Func<string, Task> _openUrl;
    private KnowledgeNode? _selected;

    public KnowledgeGraphWindow(KnowledgeGraphData graph, Func<string, Task> openUrl)
    {
        _graph = graph;
        _openUrl = openUrl;
        InitializeComponent();
        RenderGraph();
    }

    private void RenderGraph()
    {
        GraphCanvas.Children.Clear();
        var query = SearchBox.Text.StartsWith("Поиск по", StringComparison.Ordinal) ? string.Empty : SearchBox.Text.Trim();
        var nodes = _graph.Nodes.Where(x => string.IsNullOrWhiteSpace(query) ||
            (x.Title + " " + x.Url + " " + x.Summary + " " + string.Join(" ", x.Keywords))
            .Contains(query, StringComparison.OrdinalIgnoreCase)).Take(140).ToArray();
        var positions = new Dictionary<string, Point>();
        const double cx = 650, cy = 440;
        for (var i = 0; i < nodes.Length; i++)
        {
            var ring = i / 24;
            var inRing = i % 24;
            var count = Math.Min(24, nodes.Length - ring * 24);
            var angle = count <= 1 ? 0 : inRing * Math.PI * 2 / count + ring * 0.27;
            var radius = 95 + ring * 125;
            positions[nodes[i].Id] = new Point(cx + Math.Cos(angle) * radius, cy + Math.Sin(angle) * radius);
        }

        foreach (var edge in _graph.Edges.Where(x => positions.ContainsKey(x.SourceId) && positions.ContainsKey(x.TargetId)))
        {
            var a = positions[edge.SourceId]; var b = positions[edge.TargetId];
            GraphCanvas.Children.Add(new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(45 + edge.Score * 130, 45, 175), 54, 215, 196)),
                StrokeThickness = 1 + edge.Score * 2,
                ToolTip = edge.Relation
            });
        }

        foreach (var node in nodes)
        {
            var point = positions[node.Id];
            var button = new Button
            {
                Content = Short(node.Title, 28), Tag = node, Width = 132, Height = 42,
                Padding = new Thickness(6, 3, 6, 3), ToolTip = node.Title,
                Background = new SolidColorBrush(Color.FromRgb(17, 40, 47)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(43, 119, 114)),
                Foreground = (Brush)FindResource("TextBrush"), FontSize = 10
            };
            button.Click += Node_Click;
            Canvas.SetLeft(button, point.X - button.Width / 2);
            Canvas.SetTop(button, point.Y - button.Height / 2);
            GraphCanvas.Children.Add(button);
        }
        StatsText.Text = $"Узлов: {_graph.Nodes.Count} · связей: {_graph.Edges.Count} · показано: {nodes.Length}";
    }

    private void Node_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not KnowledgeNode node) return;
        _selected = node;
        NodeTitleText.Text = node.Title;
        NodeMetaText.Text = $"Посещений: {node.VisitCount} · {node.LastVisitedAtUtc.ToLocalTime():g}";
        NodeDetailsText.Text = node.Url + "\n\n" + node.Summary + "\n\nТемы: " + string.Join(", ", node.Keywords);
        OpenNodeButton.IsEnabled = true;
    }

    private async void OpenNode_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        await _openUrl(_selected.Url);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsLoaded) RenderGraph();
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SearchBox.Text.StartsWith("Поиск по", StringComparison.Ordinal)) SearchBox.Clear();
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private static string Short(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
