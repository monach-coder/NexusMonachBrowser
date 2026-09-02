using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace NexusMonach.Controls;

/// <summary>
/// Кибер-глобус: объёмная крутящаяся планета глазами хакера — каркасная
/// сетка меридианов и параллелей в акве, точки данных на орбитах, свечение.
/// 3D-иллюзия достигается анимацией ширины меридианов (cos-закон сферической
/// проекции), без тяжёлого Viewport3D.
/// Оптимизация (фикс подтормаживания): кисти заморожены и закэшированы —
/// OnRender не создаёт объектов; 24 FPS вместо 60 при том же визуальном темпе
/// (скорость задана в градусах/сек); пауза при свёрнутом окне.
/// </summary>
public class CyberGlobe : Control
{
    private const int MeridianCount = 8;
    private const int ParallelCount = 5;
    private const int DataPointCount = 12;
    private const double FramesPerSecond = 24;
    private const int DotAlphaSteps = 16;

    private DispatcherTimer? _animation;
    private DateTime _lastFrameUtc = DateTime.UtcNow;
    private double _rotation;
    private Window? _hostWindow;
    private readonly (double Lat, double Lon, double Size)[]
        _dataPoints = new (double, double, double)[DataPointCount];

    // Кэш рендера: пересобирается только при смене WireColor или размера.
    private bool _cacheValid;
    private double _cacheSize;
    private Pen _bodyPen = null!;
    private Pen _parallelPen = null!;
    private Pen _meridianFrontPen = null!;
    private Pen _meridianBackPen = null!;
    private Pen _equatorPen = null!;
    private SolidColorBrush _glowBrush = null!;
    private SolidColorBrush _bodyBrush = null!;
    private RadialGradientBrush _shadeBrush = null!;
    private readonly SolidColorBrush[] _dotBrushes = new SolidColorBrush[DotAlphaSteps];

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
            Interval = TimeSpan.FromMilliseconds(1000 / FramesPerSecond)
        };
        _animation.Tick += (_, _) =>
        {
            if (!IsVisible) return;
            var utc = DateTime.UtcNow;
            // RotationSpeed исторически «градусов за кадр при 60 FPS» —
            // переводим в градусы/сек, чтобы темп не зависел от частоты таймера.
            _rotation = (_rotation + RotationSpeed * 60 * (utc - _lastFrameUtc).TotalSeconds) % 360;
            _lastFrameUtc = utc;
            InvalidateVisual();
        };
        Loaded += OnGlobeLoaded;
        Unloaded += OnGlobeUnloaded;
    }

    /// <summary>Скорость вращения в градусах за кадр при 60 FPS (×60 = °/сек).</summary>
    public double RotationSpeed { get; set; } = 0.8;

    /// <summary>Цвет каркаса (по умолчанию MonachAqua).</summary>
    public static readonly DependencyProperty WireColorProperty =
        DependencyProperty.Register(nameof(WireColor), typeof(Color),
            typeof(CyberGlobe), new PropertyMetadata(
                Color.FromRgb(0x36, 0xD7, 0xC4), (d, _) => ((CyberGlobe)d)._cacheValid = false));

    public Color WireColor
    {
        get => (Color)GetValue(WireColorProperty);
        set => SetValue(WireColorProperty, value);
    }

    private void OnGlobeLoaded(object sender, RoutedEventArgs e)
    {
        _lastFrameUtc = DateTime.UtcNow;
        _animation?.Start();
        // Свёрнутое окно не видно — вращаем только когда окно на экране.
        _hostWindow = Window.GetWindow(this);
        if (_hostWindow is not null)
            _hostWindow.StateChanged += OnHostStateChanged;
    }

    private void OnGlobeUnloaded(object sender, RoutedEventArgs e)
    {
        _animation?.Stop();
        if (_hostWindow is not null)
        {
            _hostWindow.StateChanged -= OnHostStateChanged;
            _hostWindow = null;
        }
    }

    private void OnHostStateChanged(object? sender, EventArgs e)
    {
        if (_animation is null) return;
        if (_hostWindow?.WindowState == WindowState.Minimized)
            _animation.Stop();
        else if (IsLoaded)
        {
            _lastFrameUtc = DateTime.UtcNow;
            _animation.Start();
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void EnsureCache(double size)
    {
        if (_cacheValid && Math.Abs(size - _cacheSize) < 1) return;

        var wire = WireColor;
        _glowBrush = Frozen(Color.FromArgb(30, wire.R, wire.G, wire.B));
        _bodyBrush = Frozen(Color.FromArgb(200, 0x0B, 0x10, 0x18));
        var wireBrush = Frozen(wire);

        _bodyPen = new Pen(wireBrush, Math.Max(1, size / 100));
        _parallelPen = new Pen(Frozen(Color.FromArgb(102, wire.R, wire.G, wire.B)),
            Math.Max(0.5, size / 200));
        _meridianFrontPen = new Pen(Frozen(Color.FromArgb(178, wire.R, wire.G, wire.B)),
            Math.Max(0.5, size / 150));
        _meridianBackPen = new Pen(Frozen(Color.FromArgb(64, wire.R, wire.G, wire.B)),
            Math.Max(0.5, size / 150));
        _equatorPen = new Pen(Frozen(Color.FromArgb(153, wire.R, wire.G, wire.B)),
            Math.Max(1.2, size / 80));
        _bodyPen.Freeze();
        _parallelPen.Freeze();
        _meridianFrontPen.Freeze();
        _meridianBackPen.Freeze();
        _equatorPen.Freeze();

        for (var i = 0; i < DotAlphaSteps; i++)
            _dotBrushes[i] = Frozen(Color.FromArgb((byte)(140 + 100 * i / (DotAlphaSteps - 1)), wire.R, wire.G, wire.B));

        var shade = new RadialGradientBrush
        {
            GradientOrigin = new Point(0.35, 0.35),
            Center = new Point(0.5, 0.5),
            RadiusX = 0.5,
            RadiusY = 0.5
        };
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.0));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(60, 0, 0, 0), 0.7));
        shade.GradientStops.Add(new GradientStop(Color.FromArgb(140, 0, 0, 0), 1.0));
        shade.Freeze();
        _shadeBrush = shade;

        _cacheSize = size;
        _cacheValid = true;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size < 4) return;
        EnsureCache(size);
        var center = size / 2;
        var radius = center - Math.Max(2, size * 0.02);

        // ── Свечение (ореол) ────────────────────────────────────────
        dc.DrawEllipse(_glowBrush, null, new Point(center, center), radius * 1.08, radius * 1.08);

        // ── Тело планеты ────────────────────────────────────────────
        dc.DrawEllipse(_bodyBrush, _bodyPen, new Point(center, center), radius, radius);

        // ── Параллели (горизонтальные линии — статичны) ─────────────
        for (var p = 1; p <= ParallelCount; p++)
        {
            var lat = -Math.PI / 2 + Math.PI * p / (ParallelCount + 1);
            var y = center - radius * Math.Sin(lat);
            var r = radius * Math.Cos(lat);
            if (r < 1) continue;
            dc.DrawEllipse(null, _parallelPen, new Point(center, y), r, Math.Max(1, r * 0.12));
        }

        // ── Меридианы (вертикальные — вращаются!) ───────────────────
        for (var m = 0; m < MeridianCount; m++)
        {
            var lon = (_rotation * Math.PI / 180) + m * Math.PI * 2 / MeridianCount;
            var cosLon = Math.Cos(lon);
            var absCos = Math.Abs(cosLon);
            if (absCos < 0.03) continue; // меридиан на ребре — невидим

            var w = radius * 2 * absCos;
            dc.DrawEllipse(null,
                cosLon > 0 ? _meridianFrontPen : _meridianBackPen,
                new Point(center, center), w / 2, radius);
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

            var dotRadius = dotSize * (0.6 + depth * 0.6);
            dc.DrawEllipse(_dotBrushes[(int)(depth * (DotAlphaSteps - 1))], null,
                new Point(x, y), dotRadius, dotRadius);
        }

        // ── Экватор (яркая линия) ───────────────────────────────────
        dc.DrawEllipse(null, _equatorPen, new Point(center, center), radius * 0.95, radius * 0.14);

        // ── Терминатор (светотень для объёма) ───────────────────────
        dc.DrawEllipse(_shadeBrush, null, new Point(center, center), radius, radius);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = Math.Min(availableSize.Width, availableSize.Height);
        if (double.IsInfinity(size) || double.IsNaN(size) || size < 1)
            size = 150;
        return new Size(size, size);
    }
}
