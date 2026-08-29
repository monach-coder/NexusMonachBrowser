using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using NexusMonach.Services;

namespace NexusMonach.Views;

/// <summary>
/// Окно «Заметки и фрагменты»: дерево пометок по сайтам и страницам,
/// правка заметок, удаление, воспроизведение видео-фрагментов и экспорт
/// всего дерева в Markdown с сохранением структуры и стилей.
/// </summary>
public partial class AnnotationsWindow : Window
{
    /// <summary>Строка списка: пометка (композиция) плюс поля привязки.</summary>
    public sealed class Row
    {
        public required PageAnnotation Source { get; init; }
        public Guid Id => Source.Id;
        public AnnotationKind Kind => Source.Kind;
        public string Quote => Source.Quote;
        public string Note { get; set; } = string.Empty;
        public string MediaPath => Source.MediaPath;
        public required string SiteHeader { get; init; }
        public required string PageHeader { get; init; }
        public required string Stamp { get; init; }
        public Brush ColorBrush { get; init; } = Brushes.Transparent;
        public Visibility ColorVisible =>
            Kind == AnnotationKind.Highlight ? Visibility.Visible : Visibility.Collapsed;
        public Visibility NoteBoxVisible =>
            Kind == AnnotationKind.Highlight ? Visibility.Collapsed : Visibility.Visible;
        public Visibility MediaVisible =>
            Kind == AnnotationKind.VideoFragment ? Visibility.Visible : Visibility.Collapsed;
    }

    private static readonly Dictionary<HighlightColor, Brush> BrushesByColor = new()
    {
        [HighlightColor.Yellow] = new SolidColorBrush(Color.FromRgb(0xFF, 0xE0, 0x66)),
        [HighlightColor.Green] = new SolidColorBrush(Color.FromRgb(0xA3, 0xE6, 0x35)),
        [HighlightColor.Red] = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5)),
        [HighlightColor.Blue] = new SolidColorBrush(Color.FromRgb(0x93, 0xC5, 0xFD))
    };

    public AnnotationsWindow()
    {
        InitializeComponent();
        AnnotationsService.Changed += Refresh;
        Loaded += (_, _) => Refresh();
        Closed += (_, _) => AnnotationsService.Changed -= Refresh;
    }

    private void Refresh()
    {
        var rows = new List<Row>();
        foreach (var group in AnnotationsService.All
                     .GroupBy(a => new Uri(a.Url).Host, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            foreach (var page in group.GroupBy(a => a.Url, StringComparer.Ordinal)
                         .OrderByDescending(g => g.Max(x => x.CreatedUtc)))
            {
                var title = page.MaxBy(a => a.PageTitle.Length)?.PageTitle;
                if (string.IsNullOrWhiteSpace(title)) title = page.Key;
                foreach (var item in page.OrderByDescending(a => a.CreatedUtc))
                {
                    rows.Add(new Row
                    {
                        Source = item,
                        Note = item.Note,
                        SiteHeader = group.Key,
                        PageHeader = title,
                        Stamp = item.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm") +
                            (item.Kind == AnnotationKind.VideoFragment
                                ? " · видео " + TimeSpan.FromSeconds(item.VideoPositionSeconds).ToString(@"mm\:ss") +
                                  " (+" + TimeSpan.FromSeconds(item.DurationSeconds).ToString(@"mm\:ss") + ")"
                                : ""),
                        ColorBrush = BrushesByColor.GetValueOrDefault(item.Color, Brushes.Yellow)
                    });
                }
            }
        }
        ItemsList.ItemsSource = rows;
    }

    private void NoteBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is TextBox { DataContext: Row row } box)
            AnnotationsService.UpdateNote(row.Id, box.Text.Trim());
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is Row row)
            AnnotationsService.Remove(row.Id);
    }

    private void OpenMedia_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not Row row ||
            row.MediaPath.Length == 0) return;
        var full = Path.Combine(AppPaths.AppRoot, row.MediaPath);
        if (!File.Exists(full)) return;
        try { Process.Start(new ProcessStartInfo(full) { UseShellExecute = true }); }
        catch (Exception ex) { CrashReportService.RecordNonFatal("annotations", "open-media", ex); }
    }

    /// <summary>
    /// Экспорт: Markdown-файл плюс копия видео-фрагментов в notes-media
    /// рядом с документом — относительные ссылки из Markdown работают.
    /// </summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var all = AnnotationsService.All.ToList();
        if (all.Count == 0)
        {
            GlassDialogWindow.Show(this, "Пометок пока нет — выделяйте текст на страницах и пользуйтесь панелью.",
                "Заметки", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new SaveFileDialog
        {
            Title = "Экспорт заметок в Markdown",
            Filter = "Markdown (*.md)|*.md",
            FileName = "nexus-заметки-" + DateTime.Now.ToString("yyyy-MM-dd") + ".md"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, AnnotationsService.BuildMarkdown(all));
            var mediaTarget = Path.Combine(Path.GetDirectoryName(dialog.FileName)!, "notes-media");
            foreach (var media in all.Where(a => a.Kind == AnnotationKind.VideoFragment && a.MediaPath.Length > 0))
            {
                var source = Path.Combine(AppPaths.AppRoot, media.MediaPath);
                if (!File.Exists(source)) continue;
                Directory.CreateDirectory(mediaTarget);
                File.Copy(source, Path.Combine(mediaTarget, Path.GetFileName(media.MediaPath)), true);
            }
            GlassDialogWindow.Show(this,
                "Экспортировано: " + dialog.FileName +
                "\nВидео-фрагменты лежат рядом в notes-media — относительные ссылки уже работают." +
                "\nДокумент открывается Obsidian, VS Code, Typora и любым редактором с поддержкой Markdown.",
                "Заметки", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            CrashReportService.RecordNonFatal("annotations", "export", ex);
            GlassDialogWindow.Show(this, "Экспорт не удался: " + ex.Message,
                "Заметки", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
