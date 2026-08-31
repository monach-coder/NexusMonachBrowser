using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NexusMonach.Controls;

/// <summary>
/// Кибер-глобус: объёмная крутящаяся планета глазами хакера — каркасная
/// сетка меридианов и параллелей в акве, точки данных на орбитах, свечение.
/// 3D-иллюзия достигается анимацией ширины меридианов (cos-закон сферической
/// проекции), без тяжёлого Viewport3D — 60 FPS на любом размере.
/// </summary>
public class CyberGlobe : Control
{
    private const int MeridianCount = 8;
    private const int ParallelCount = 5;
    private const int DataPointCount = 12;

    private readonly DispatcherTimer? _animation;
    private double _rotation;
    private readonly (double Lat, double Lon, double Size)[]
        _dataPoints = new (double, double, double)[DataPointCount];

    public CyberGlobe()
    {
        var random = new Random(Environment.TickCount ^ unchecked((int)DateTime.Now.Ticks));
        for (var i = 0; i < DataPointCount; i++)
        {
            _dataPoints[i] = (
                Lat: (random.NextDouble() - 0.5) * Math.PI * 0.8,
                Lon: random.NextDouble() * Math.PI * 2,
                Size: 1.5 + random.NextDouble() * 2.5);
        }

        _animation = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(16) // ~60 FPS
        };
        _animation.Tick += (_, _) =>
        {
            _rotation = (_rotation + RotationSpeed) % 360;
            InvalidateVisual();
        };
        _animation.Start();
        Loaded += (_, _) => _animation.Start();
        Unloaded += (_, _) => _animation.Stop();
    }

    /// <summary>Скорость вращения в градусах за кадр.</summary>
    public double RotationSpeed { get; set; } = 0.8;

    /// <summary>Цвет каркаса (по умолчанию MonachAqua).</summary>
    public static readonly DependencyProperty WireColorProperty =
        DependencyProperty.Register(nameof(WireColor), typeof(Color),
            typeof(CyberGlobe), new PropertyMetadata(Color.FromRgb(0x36, 0xD7, 0xC4)));

    public Color WireColor
    {
        get => (Color)GetValue(WireColorProperty);
        set => SetValue(WireColorProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4) return;
        var center = size / 2;
        var radius = center - Math.Max(2, size * 0.02);

        var wire = WireColor;
        var wireBrush = new SolidColorBrush(wire);
        var glowBrush = new SolidColorBrush(Color.FromArgb(30, wire.R, wire.G, wire.B));
        var bodyBrush = new SolidColorBrush(Color.FromArgb(200, 0x0B, 0x10, 0x18));

        // ── Свечение (ореол) ────────────────────────────────────────
        dc.DrawEllipse(glowBrush, null, new Point(center, center), radius * 1.08, radius * 1.08);

        // ── Тело планеты ────────────────────────────────────────────
        dc.DrawEllipse(bodyBrush,
            new Pen(wireBrush, Math.Max(1, size / 100)), new Point(center, center), radius, radius);

        // ── Параллели (горизонтальные линии — статичны) ─────────────
        for (var p = 1; p <= ParallelCount; p++)
        {
            var lat = -Math.PI / 2 + Math.PI * p / (ParallelCount + 1);
            var y = center - radius * Math.Sin(lat);
            var r = radius * Math.Cos(lat);
            if (r < 1) continue;
            var parallelBrush = new SolidColorBrush(Color.FromArgb(102, wire.R, wire.G, wire.B));
            dc.DrawEllipse(null,
                new Pen(parallelBrush, Math.Max(0.5, size / 200)),
                new Point(center, y), r, Math.Max(1, r * 0.12));
        }

        // ── Меридианы (вертикальные — вращаются!) ───────────────────
        for (var m = 0; m < MeridianCount; m++)
        {
            var lon = (_rotation * Math.PI / 180) + m * Math.PI * 2 / MeridianCount;
            var cosLon = Math.Cos(lon);
            var absCos = Math.Abs(cosLon);
            if (absCos < 0.03) continue; // меридиан на ребре — невидим

            var w = radius * 2 * absCos;
            var x = center - w / 2;

            // Меридианы на передней стороне ярче, на задней — тусклее.
            var opacity = cosLon > 0 ? 0.7 : 0.25;
            var meridianBrush = new SolidColorBrush(Color.FromArgb(
                (byte)(255 * opacity), wire.R, wire.G, wire.B));
            var pen = new Pen(meridianBrush, Math.Max(0.5, size / 150));

            dc.DrawEllipse(null, pen, new Point(center, center), w / 2, radius);
        }

        // ── Точки данных (орбитальные маркеры) ──────────────────────
        for (var i = 0; i < DataPointCount; i++)
        {
            var (lat, baseLon, dotSize) = _dataPoints[i];
            var lon = baseLon + _rotation * Math.PI / 180;
            var cosLon = Math.Cos(lon);
            var sinLat = Math.Sin(lat);

            // Видна только передняя полусфера.
            if (cosLon <= 0) continue;

            var x = center + radius * cosLon * Math.Cos(lat) * 0.9;
            var y = center - radius * sinLat * 0.9;
            var depth = (cosLon + 1) / 2; // 0 (ребро) → 1 (центр)

            var dotBrush = new SolidColorBrush(Color.FromArgb(
                (byte)(140 + 100 * depth), wire.R, wire.G, wire.B));
            dc.DrawEllipse(dotBrush, null,
                new Point(x, y), dotSize * (0.6 + depth * 0.6), dotSize * (0.6 + depth * 0.6));
        }

        // ── Экватор (яркая линия) ───────────────────────────────────
        var eqBrush = new SolidColorBrush(Color.FromArgb(153, wire.R, wire.G, wire.B));
        var eqPen = new Pen(eqBrush, Math.Max(1.2, size / 80));
        dc.DrawEllipse(null, eqPen, new Point(center, center), radius * 0.95, radius * 0.14);

        // ── Терминатор (светотень для объёма) ───────────────────────
        var shadeBrush = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.35),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        shadeBrush.GradientStops.Add(new GradientStop(
            Color.FromArgb(0, 0, 0, 0), 0.0));
        shadeBrush.GradientStops.Add(new GradientStop(
            Color.FromArgb(60, 0, 0, 0), 0.7));
        shadeBrush.GradientStops.Add(new GradientStop(
            Color.FromArgb(140, 0, 0, 0), 1.0));
        dc.DrawEllipse(shadeBrush, null, new Point(center, center), radius, radius);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(size) || double.IsNaN(size) || size < 1)
            size = 150;
        return new Size(size, size);
    }
}
