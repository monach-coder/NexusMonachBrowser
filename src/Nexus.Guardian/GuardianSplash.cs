using System.Drawing;

namespace Nexus.Guardian;

/// <summary>
/// Мгновенный отклик на запуск. Guardian проверяет почти гигабайт критических
/// файлов, и без этого окна первый щелчок по ярлыку выглядит как «ничего не
/// произошло» — пользователь запускает вторую копию вместо ожидания.
/// </summary>
internal sealed class GuardianSplash : Form
{
    private const int TrackLeft = 110;
    private const int TrackWidth = 200;
    private const int RunnerWidth = 34;
    private readonly System.Windows.Forms.Timer _animation = new();
    private readonly Label _runner = new();
    private Label _statusReference = new();
    private int _runnerPosition;
    private int _runnerDirection = 1;

    public GuardianSplash()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(420, 168);
        BackColor = Color.FromArgb(11, 16, 24);

        var brand = new Label
        {
            Text = "NEXUS MONACH",
            ForeColor = Color.FromArgb(238, 244, 248),
            Font = new Font("Segoe UI Semibold", 15.75f, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(0, 34, 420, 34)
        };
        var status = new Label
        {
            Text = "Guardian проверяет целостность браузера…",
            ForeColor = Color.FromArgb(145, 162, 180),
            Font = new Font("Segoe UI", 9.75f),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(0, 70, 420, 24)
        };
        _statusReference = status;
        var track = new Label
        {
            BackColor = Color.FromArgb(28, 38, 52),
            Bounds = new Rectangle(TrackLeft, 108, TrackWidth, 5),
            AutoSize = false
        };
        _runner.BackColor = Color.FromArgb(54, 215, 196);
        _runner.SetBounds(TrackLeft, 107, RunnerWidth, 7);

        var accent = new Label
        {
            BackColor = Color.FromArgb(54, 215, 196),
            Bounds = new Rectangle(196, 140, 28, 3),
            AutoSize = false
        };

        _animation.Interval = 25;
        _animation.Tick += (_, _) => AdvanceRunner();

        SuspendLayout();
        Controls.Add(track);
        Controls.Add(_runner);
        Controls.Add(brand);
        Controls.Add(status);
        Controls.Add(accent);
        ResumeLayout();

        Paint += DrawFrame;
    }

    /// <summary>Обновляет строку состояния сплэша (потокобезопасно).</summary>
    public void SetStatus(string text)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStatus(text));
            return;
        }
        _statusReference.Text = text;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        _animation.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Stop();
            _animation.Dispose();
        }
        base.Dispose(disposing);
    }

    private void AdvanceRunner()
    {
        _runnerPosition += 6 * _runnerDirection;
        if (_runnerPosition <= 0)
        {
            _runnerPosition = 0;
            _runnerDirection = 1;
        }
        else if (_runnerPosition >= TrackWidth - RunnerWidth)
        {
            _runnerPosition = TrackWidth - RunnerWidth;
            _runnerDirection = -1;
        }
        _runner.Left = TrackLeft + _runnerPosition;
    }

    private void DrawFrame(object? sender, PaintEventArgs e)
    {
        using var pen = new Pen(Color.FromArgb(85, 54, 215, 196));
        e.Graphics.DrawRectangle(pen, 0, 0, ClientSize.Width - 1, ClientSize.Height - 1);
    }
}
