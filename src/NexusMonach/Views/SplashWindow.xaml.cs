using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using SplashPath = System.Windows.Shapes.Path;

namespace NexusMonach.Views;

/// <summary>
/// Круглый пульсирующий сплэш — единое стартовое окно браузера. Guardian уже
/// показал проверку целостности и ход обновления таким же круглым окном с
/// секторами; здесь эстафета продолжается: сектор целостности закрыт статусом
/// из переменной окружения, обновление и загрузку ведёт SplashUpdateWatcher,
/// сектор «запуск» закрывается перед появлением главного окна.
/// </summary>
public partial class SplashWindow : Window
{
    private const double CenterX = 180;
    private const double CenterY = 200;
    private const double RingRadius = 157;
    private const double SectorGapDeg = 6;
    private const double SectorSweepDeg = 90 - SectorGapDeg;
    private const double CometDeg = 26;
    private const int SectorCount = 4;
    private static readonly string[] SectorNames = ["целостность", "обновление", "загрузка", "запуск"];

    private readonly SplashPath[] _tracks;
    private readonly SplashPath[] _fills;
    // Сектор: -2 ждёт очереди, -1 неопределённый ход (комета), 0–99 заполнение, 100 готово.
    private readonly int[] _state = new int[SectorCount];
    private readonly DispatcherTimer _comet = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private double _cometPhase;

    public SplashWindow()
    {
        InitializeComponent();
        _tracks = [Track0, Track1, Track2, Track3];
        _fills = [Fill0, Fill1, Fill2, Fill3];
        for (var i = 0; i < SectorCount; i++)
        {
            _state[i] = -2;
            _tracks[i].Data = SectorGeometry(i * 90 + SectorGapDeg / 2, SectorSweepDeg);
            _fills[i].Data = Geometry.Empty;
        }
        _comet.Tick += (_, _) =>
        {
            _cometPhase = (_cometPhase + 0.035) % 1.0;
            for (var i = 0; i < SectorCount; i++)
                if (_state[i] == -1)
                    RedrawSector(i);
        };
    }

    private async void Grid_Loaded(object sender, RoutedEventArgs e)
    {
        ((Storyboard)FindResource("Pulse")).Begin(this, true);
        _comet.Start();
        // Эстафета с Guardian: круглое окно лаунчера закрывается только после
        // этой метки — старт выглядит одним непрерывным окном. Одна неудачная
        // запись (файл на мгновение занят антивирусом/индексатором) стоила бы
        // десяти секунд двойного сплэша — Guardian держит своё окно до метки.
        try
        {
            var guardian = Path.Combine(Services.AppPaths.AppRoot, "Guardian");
            Directory.CreateDirectory(guardian);
            var marker = Path.Combine(guardian, "splash-ready.json");
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"));
                    return;
                }
                catch when (attempt < 5)
                {
                    await Task.Delay(150);
                }
            }
        }
        catch { /* Guardian переживёт отсутствие метки — просто закроет окно по таймауту */ }
    }

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

    /// <summary>Золотая строка деталей: мегабайты, версия, пояснение.</summary>
    public void SetDetail(string text)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => SetDetail(text));
            return;
        }
        DetailText.Text = text ?? string.Empty;
    }

    /// <summary>Активировать сектор: -1 — неопределённый ход, 0–100 — заполнение.</summary>
    public void ActivateSector(int index, int percent)
    {
        if (index is < 0 or >= SectorCount) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => ActivateSector(index, percent));
            return;
        }
        _state[index] = Math.Clamp(percent, -1, 99);
        RedrawSector(index);
        UpdateStagesRow();
    }

    /// <summary>Сектор завершён: полная мягкая подсветка.</summary>
    public void CompleteSector(int index)
    {
        if (index is < 0 or >= SectorCount) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => CompleteSector(index));
            return;
        }
        _state[index] = 100;
        RedrawSector(index);
        UpdateStagesRow();
    }

    /// <summary>Дуга сектора: углы от 12 часов по часовой стрелке.</summary>
    private static Geometry SectorGeometry(double startDeg, double sweepDeg)
    {
        Point Polar(double deg) => new(
            CenterX + RingRadius * Math.Sin(deg * Math.PI / 180.0),
            CenterY - RingRadius * Math.Cos(deg * Math.PI / 180.0));
        var figure = new PathFigure { StartPoint = Polar(startDeg) };
        figure.Segments.Add(new ArcSegment
        {
            Point = Polar(startDeg + sweepDeg),
            Size = new Size(RingRadius, RingRadius),
            IsLargeArc = sweepDeg > 180,
            SweepDirection = SweepDirection.Clockwise
        });
        return new PathGeometry(new[] { figure });
    }

    private void RedrawSector(int index)
    {
        var start = index * 90 + SectorGapDeg / 2;
        var fill = _fills[index];
        switch (_state[index])
        {
            case 100:
                fill.Data = SectorGeometry(start, SectorSweepDeg);
                fill.Opacity = 0.45;
                break;
            case >= 0:
                fill.Data = SectorGeometry(start, SectorSweepDeg * _state[index] / 100.0);
                fill.Opacity = 1;
                break;
            case -1:
                // Неопределённый ход: комета бежит по сектору туда-обратно.
                var ping = 0.5 - 0.5 * Math.Cos(_cometPhase * Math.PI * 2);
                var span = SectorSweepDeg - CometDeg;
                fill.Data = SectorGeometry(start + span * ping, CometDeg);
                fill.Opacity = 1;
                break;
            default:
                fill.Data = Geometry.Empty;
                break;
        }
    }

    private void UpdateStagesRow()
    {
        var parts = new string[SectorCount];
        for (var i = 0; i < SectorCount; i++)
            parts[i] = (_state[i] == 100 ? "●" : _state[i] != -2 ? "◉" : "○") + " " + SectorNames[i];
        StagesText.Text = string.Join("   ", parts);
    }
}
