using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using NexusMonach.Models;
using NexusMonach.Services;

namespace NexusMonach.Views;

public partial class KnowledgeGraphWindow : Window
{
    private sealed record RelatedItem(KnowledgeNode Node, string Display);
    private KnowledgeGraphData _graph;
    private readonly Func<string, Task> _openUrl;
    private readonly DispatcherTimer _searchTimer;
    private KnowledgeNode? _selected;
    private IReadOnlyList<KnowledgeSearchHit> _hits = [];
    private CancellationTokenSource? _searchCancellation;

    public KnowledgeGraphWindow(KnowledgeGraphData graph, Func<string, Task> openUrl)
    {
        _graph = graph;
        _openUrl = openUrl;
        InitializeComponent();
        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(420) };
        _searchTimer.Tick += async (_, _) => { _searchTimer.Stop(); await SearchAndRenderAsync(); };
        Loaded += async (_, _) => await SearchAndRenderAsync();
    }

    private async Task SearchAndRenderAsync()
    {
        _searchCancellation?.Cancel();
        _searchCancellation = new CancellationTokenSource();
        var query = SearchBox.Text.StartsWith("Найти страницу", StringComparison.Ordinal) ? string.Empty : SearchBox.Text.Trim();
        try
        {
            _graph = KnowledgeGraphService.Snapshot();
            InsightText.Text = string.IsNullOrWhiteSpace(query) ? "Недавние темы и маршруты" : "Nexus Semantics ищет близкий смысл…";
            _hits = await KnowledgeGraphService.SearchAsync(query, _searchCancellation.Token);
            var period = GetSelectedPeriod();
            if (period > 0)
                _hits = _hits.Where(x => x.Node.LastVisitedAtUtc >= DateTime.UtcNow.AddDays(-period)).ToArray();
            RenderGraph(_hits.Take(180).Select(x => x.Node).ToArray());
            InsightText.Text = string.IsNullOrWhiteSpace(query)
                ? BuildInsight(_hits.Select(x => x.Node))
                : $"Найдено по смыслу: {_hits.Count} · лучший результат: {(_hits.FirstOrDefault()?.MatchReason ?? "—")}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { InsightText.Text = "Поиск графа: " + ex.Message; }
    }

    private void RenderGraph(IReadOnlyList<KnowledgeNode> nodes)
    {
        GraphCanvas.Children.Clear();
        EmptyPanel.Visibility = nodes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (nodes.Count == 0)
        {
            StatsText.Text = $"Узлов: {_graph.Nodes.Count} · связей: {_graph.Edges.Count} · исследований: {_graph.ResearchSessions.Count}";
            return;
        }

        var ids = nodes.Select(x => x.Id).ToHashSet();
        var edges = _graph.Edges.Where(x => ids.Contains(x.SourceId) && ids.Contains(x.TargetId))
            .OrderByDescending(x => x.Kind == "navigation").ThenByDescending(x => x.Kind == "research")
            .ThenByDescending(x => x.Score).Take(650).ToArray();
        var positions = CalculateLayout(nodes, edges);

        foreach (var edge in edges)
        {
            if (!positions.TryGetValue(edge.SourceId, out var a) || !positions.TryGetValue(edge.TargetId, out var b)) continue;
            var navigation = edge.Kind == "navigation";
            var research = edge.Kind == "research";
            var line = new Line
            {
                X1 = a.X, Y1 = a.Y, X2 = b.X, Y2 = b.Y,
                Stroke = navigation ? new SolidColorBrush(Color.FromArgb(150, 218, 185, 106)) :
                    research ? new SolidColorBrush(Color.FromArgb(165, 156, 122, 255)) :
                    new SolidColorBrush(Color.FromArgb((byte)Math.Clamp(35 + edge.Score * 150, 35, 190), 54, 215, 196)),
                StrokeThickness = navigation ? 1.8 + Math.Min(2, edge.Strength * 0.25) :
                    research ? 1.4 + edge.Score * 1.7 : 0.8 + edge.Score * 2.4,
                ToolTip = navigation ? $"Маршрут · переходов: {edge.Strength}" :
                    research ? "Исследование · " + edge.Relation : edge.Relation
            };
            if (navigation) line.StrokeDashArray = new DoubleCollection { 5, 4 };
            if (research) line.StrokeDashArray = new DoubleCollection { 2, 3 };
            GraphCanvas.Children.Add(line);
        }

        foreach (var group in nodes.GroupBy(x => x.Topic).Where(x => !string.IsNullOrWhiteSpace(x.Key)))
        {
            var points = group.Select(x => positions[x.Id]).ToArray();
            var center = new Point(points.Average(x => x.X), points.Average(x => x.Y));
            var label = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(120, 12, 18, 26)),
                BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(9), Padding = new Thickness(9, 4, 9, 4),
                Child = new TextBlock { Text = group.Key.ToUpperInvariant(), FontSize = 9.5, Foreground = (Brush)FindResource("MutedTextBrush") },
                IsHitTestVisible = false
            };
            Canvas.SetLeft(label, center.X - 45); Canvas.SetTop(label, center.Y - 55);
            GraphCanvas.Children.Add(label);
        }

        foreach (var node in nodes)
        {
            var point = positions[node.Id];
            var size = Math.Clamp(42 + Math.Log2(node.VisitCount + 1) * 5, 44, 64);
            var color = TopicColor(node.Topic);
            var button = new Button
            {
                Content = new TextBlock { Text = Short(node.Title, 34), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center },
                Tag = node, Width = Math.Max(126, size * 2.55), Height = size,
                Padding = new Thickness(7, 4, 7, 4), ToolTip = $"{node.Title}\n{node.Summary}\nДвойной щелчок — открыть",
                Background = new SolidColorBrush(Color.FromArgb(220, (byte)(color.R / 4 + 11), (byte)(color.G / 4 + 14), (byte)(color.B / 4 + 18))),
                BorderBrush = new SolidColorBrush(Color.FromArgb(220, color.R, color.G, color.B)),
                Foreground = (Brush)FindResource("TextBrush"), FontSize = 10.5,
                BorderThickness = new Thickness(node.IsPinned ? 2.5 : 1.2)
            };
            button.Click += Node_Click;
            button.MouseDoubleClick += Node_MouseDoubleClick;
            Canvas.SetLeft(button, point.X - button.Width / 2);
            Canvas.SetTop(button, point.Y - button.Height / 2);
            GraphCanvas.Children.Add(button);
        }
        StatsText.Text = $"Память: {_graph.Nodes.Count} страниц · {_graph.Edges.Count} связей · исследований: {_graph.ResearchSessions.Count} · на карте: {nodes.Count}";
    }

    private static Dictionary<string, Point> CalculateLayout(IReadOnlyList<KnowledgeNode> nodes,
        IReadOnlyList<KnowledgeEdge> edges)
    {
        const double width = 1720, height = 1080, margin = 90;
        var groups = nodes.GroupBy(x => string.IsNullOrWhiteSpace(x.Topic) ? x.Domain : x.Topic)
            .OrderByDescending(x => x.Count()).ToArray();
        var centers = new Dictionary<string, Point>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < groups.Length; i++)
        {
            var angle = groups.Length <= 1 ? 0 : i * Math.PI * 2 / groups.Length;
            var radius = groups.Length <= 1 ? 0 : Math.Min(390, 120 + groups.Length * 18);
            centers[groups[i].Key] = new Point(width / 2 + Math.Cos(angle) * radius, height / 2 + Math.Sin(angle) * radius);
        }
        var positions = new Dictionary<string, Point>();
        foreach (var group in groups)
        {
            var center = centers[group.Key]; var list = group.ToArray();
            for (var i = 0; i < list.Length; i++)
            {
                var angle = StableHash(list[i].Id) / (double)int.MaxValue * Math.PI * 2;
                var radius = 35 + Math.Sqrt(i + 1) * 43;
                positions[list[i].Id] = new Point(center.X + Math.Cos(angle) * radius, center.Y + Math.Sin(angle) * radius);
            }
        }

        var byId = nodes.ToDictionary(x => x.Id);
        for (var iteration = 0; iteration < 70; iteration++)
        {
            var forces = nodes.ToDictionary(x => x.Id, _ => new Vector());
            for (var i = 0; i < nodes.Count; i++)
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var delta = positions[nodes[i].Id] - positions[nodes[j].Id];
                var distance = Math.Max(24, delta.Length); delta.Normalize();
                var force = Math.Min(13, 16500 / (distance * distance));
                forces[nodes[i].Id] += delta * force; forces[nodes[j].Id] -= delta * force;
            }
            foreach (var edge in edges)
            {
                var delta = positions[edge.TargetId] - positions[edge.SourceId];
                var distance = Math.Max(1, delta.Length); delta.Normalize();
                var desired = edge.Kind == "navigation" ? 175 : 145;
                var force = (distance - desired) * 0.012 * Math.Max(0.35, edge.Score);
                forces[edge.SourceId] += delta * force; forces[edge.TargetId] -= delta * force;
            }
            foreach (var node in nodes)
            {
                var center = centers[string.IsNullOrWhiteSpace(node.Topic) ? node.Domain : node.Topic];
                forces[node.Id] += (center - positions[node.Id]) * 0.007;
                var move = forces[node.Id]; if (move.Length > 11) { move.Normalize(); move *= 11; }
                var p = positions[node.Id] + move;
                positions[node.Id] = new Point(Math.Clamp(p.X, margin, width - margin), Math.Clamp(p.Y, margin, height - margin));
            }
        }
        return positions;
    }

    private void SelectNode(KnowledgeNode node)
    {
        _selected = node;
        NodeTitleText.Text = node.Title;
        NodeKindText.Text = node.PageKind + " · " + node.Topic;
        NodeVisitsText.Text = $"{node.VisitCount} посещений";
        NodeDetailsText.Text = node.Url + "\n\n" +
            (string.IsNullOrWhiteSpace(node.Summary) ? "Краткое содержание появится после смысловой индексации страницы." : node.Summary) +
            "\n\nПонятия: " + (node.Keywords.Count == 0 ? "ещё не выделены" : string.Join(", ", node.Keywords)) +
            $"\n\nВпервые: {node.CreatedAtUtc.ToLocalTime():g}\nПоследний раз: {node.LastVisitedAtUtc.ToLocalTime():g}";
        RelatedList.ItemsSource = _graph.Edges.Where(x => x.SourceId == node.Id || x.TargetId == node.Id)
            .OrderByDescending(x => x.Kind == "navigation").ThenByDescending(x => x.Score).Take(30)
            .Select(edge =>
            {
                var id = edge.SourceId == node.Id ? edge.TargetId : edge.SourceId;
                var related = _graph.Nodes.FirstOrDefault(x => x.Id == id);
                var relation = edge.Kind == "navigation" ? "маршрут" : edge.Relation;
                return related is null ? null : new RelatedItem(related, $"{related.Title}  ·  {relation}");
            }).Where(x => x is not null).DistinctBy(x => x!.Node.Id).ToArray();
        PinNodeButton.IsEnabled = true;
        PinNodeButton.Content = node.IsPinned ? "Открепить" : "Закрепить";
        OpenNodeButton.IsEnabled = true;
    }

    private void Node_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is KnowledgeNode node) SelectNode(node);
    }

    private async void Node_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Button)?.Tag is KnowledgeNode node) await _openUrl(node.Url);
    }

    private async void OpenNode_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not null) await _openUrl(_selected.Url);
    }

    private async void PinNode_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        _selected.IsPinned = !_selected.IsPinned;
        await KnowledgeGraphService.SetPinnedAsync(_selected.Id, _selected.IsPinned);
        var original = _graph.Nodes.FirstOrDefault(x => x.Id == _selected.Id);
        if (original is not null) original.IsPinned = _selected.IsPinned;
        PinNodeButton.Content = _selected.IsPinned ? "Открепить" : "Закрепить";
        await SearchAndRenderAsync();
    }

    private void RelatedList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RelatedList.SelectedItem is RelatedItem item) SelectNode(item.Node);
    }

    private async void RelatedList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (RelatedList.SelectedItem is RelatedItem item) await _openUrl(item.Node.Url);
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!IsLoaded) return;
        _searchTimer.Stop(); _searchTimer.Start();
    }

    private void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (SearchBox.Text.StartsWith("Найти страницу", StringComparison.Ordinal)) SearchBox.Clear();
    }

    private async void PeriodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await SearchAndRenderAsync();
    }

    private async void ResetView_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = "Найти страницу по смыслу, теме, названию или URL";
        PeriodBox.SelectedIndex = 0;
        _selected = null; RelatedList.ItemsSource = null;
        await SearchAndRenderAsync();
    }

    private int GetSelectedPeriod() => PeriodBox.SelectedItem is ComboBoxItem { Tag: string value } && int.TryParse(value, out var days) ? days : 0;
    private static int StableHash(string value) => value.Aggregate(17, (current, ch) => unchecked(current * 31 + ch)) & int.MaxValue;
    private static Color TopicColor(string value)
    {
        var hash = StableHash(value); byte[] paletteR = [54, 218, 116, 87, 202, 66]; byte[] paletteG = [215, 185, 145, 164, 109, 176]; byte[] paletteB = [196, 106, 230, 226, 166, 210];
        var index = hash % paletteR.Length; return Color.FromRgb(paletteR[index], paletteG[index], paletteB[index]);
    }
    private static string BuildInsight(IEnumerable<KnowledgeNode> nodes)
    {
        var topics = nodes.GroupBy(x => x.Topic).OrderByDescending(x => x.Count()).Take(3).Select(x => x.Key).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        return topics.Length == 0 ? "Открывай страницы — Nexus начнёт связывать знания" : "Активные созвездия: " + string.Join(" · ", topics);
    }
    private void Close_Click(object sender, RoutedEventArgs e) => Close();
    private static string Short(string value, int max) => value.Length <= max ? value : value[..(max - 1)] + "…";
}
