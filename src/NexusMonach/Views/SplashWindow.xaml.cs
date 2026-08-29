using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace NexusMonach.Views;

/// <summary>
/// Круглый пульсирующий сплэш — единое стартовое окно: эмблема в дыхании,
/// статус целостности Guardian (приходит переменной окружения) и кольцо-шкала
/// обновления с этапами (читаются из прогресс-файла скрытой проверки).
/// </summary>
public partial class SplashWindow : Window
{
    // Круг 314px: периметр в единицах толщины штриха (4px) ≈ 246.6.
    private const double RingUnits = 246.6;

    public SplashWindow() => InitializeComponent();

    private void Grid_Loaded(object sender, RoutedEventArgs e) =>
        ((Storyboard)FindResource("Pulse")).Begin(this, true);

    /// <summary>Статус Guardian: целостность, затем ход обновления.</summary>
    public void SetStatus(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStatus(text));
            return;
        }
        StatusText.Text = text;
    }

    /// <summary>Кольцо-шкала: percent ≥ 0 — дуга заполнения; -1 — скрыть.</summary>
    public void SetProgress(int percent, string detail)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetProgress(percent, detail));
            return;
        }
        if (percent >= 0)
        {
            var fraction = Math.Clamp(percent, 0, 100) / 100.0;
            ProgressArc.Visibility = Visibility.Visible;
            ProgressArc.StrokeDashArray = new DoubleCollection { fraction * RingUnits, RingUnits };
        }
        else
            ProgressArc.Visibility = Visibility.Collapsed;
        DetailText.Text = detail ?? string.Empty;
    }

    /// <summary>Временная шкала этапов (1–4), точками под статусом.</summary>
    public void SetStage(int stage)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetStage(stage));
            return;
        }
        string[] names = ["проверка", "загрузка", "установка", "запуск"];
        var marks = new string[4];
        for (var i = 0; i < 4; i++)
            marks[i] = i + 1 < stage ? "●" : i + 1 == stage ? "◉" : "○";
        StagesText.Text = string.Join("  ", marks.Zip(names, (mark, name) => mark + " " + name));
    }
}
