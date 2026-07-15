using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace NexusMonach.Views;

public partial class SplashWindow : Window
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(60);
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private DateTime _started;

    public SplashWindow()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => UpdateCountdown();
        Closed += (_, _) => _timer.Stop();
    }

    private void Grid_Loaded(object sender, RoutedEventArgs e)
    {
        _started = DateTime.UtcNow;
        UpdateCountdown();
        _timer.Start();
    }

    private void UpdateCountdown()
    {
        var elapsed = DateTime.UtcNow - _started;
        var remaining = Math.Clamp(1 - elapsed.TotalSeconds / Duration.TotalSeconds, 0, 1);
        CountdownText.Text = Math.Ceiling(remaining * Duration.TotalSeconds).ToString("0") + " с";
        CountdownRing.Data = CreateRemainingArc(remaining);
    }

    private static Geometry CreateRemainingArc(double remaining)
    {
        const double center = 180;
        const double radius = 154;
        if (remaining <= .001) return Geometry.Empty;
        if (remaining >= .999)
            return new EllipseGeometry(new Point(center, center), radius, radius);

        // The disappearing edge travels clockwise from twelve o'clock.
        var startAngle = -90 + (1 - remaining) * 360;
        const double endAngle = 269.9;
        Point At(double degrees)
        {
            var radians = degrees * Math.PI / 180;
            return new Point(center + radius * Math.Cos(radians), center + radius * Math.Sin(radians));
        }
        var figure = new PathFigure { StartPoint = At(startAngle), IsClosed = false };
        figure.Segments.Add(new ArcSegment(At(endAngle), new Size(radius, radius), 0,
            remaining > .5, SweepDirection.Clockwise, true));
        return new PathGeometry(new[] { figure });
    }
}
