using System.Drawing;

namespace Nexus.Guardian;

/// <summary>
/// Мгновенный отклик на запуск. Guardian проверяет почти гигабайт критических
/// файлов, и без этого окна первый щелчок по ярлыку выглядит как «ничего не
/// произошло» — пользователь запускает вторую копию вместо ожидания.
/// При обновлении превращается в шкалу: этап, проценты и мегабайты —
/// тишина на минуту выглядит как вылет, шкала объясняет, что живо.
/// </summary>
internal sealed class GuardianSplash : Form
{
    private const int TrackLeft = 60;
    private const int TrackWidth = 300;
    private const int BarHeight = 8;
    private readonly System.Windows.Forms.Timer _animation = new();
    private readonly Label _runner = new();
    private readonly Label _fill = new();
    private readonly Label _detail = new();
    private readonly Label _stages = new();
    private Label _statusReference = new();
    private int _runnerPosition;
    private int _runnerDirection = 1;
    private int _percent = -1;

    public GuardianSplash()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        ClientSize = new Size(420, 208);
        BackColor = Color.FromArgb(11, 16, 24);

        var brand = new Label
        {
            Text = "NEXUS MONACH",
            ForeColor = Color.FromArgb(238, 244, 248),
            Font = new Font("Segoe UI Semibold", 15.75f, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(0, 26, 420, 34)
        };
        var status = new Label
        {
            Text = "Guardian проверяет целостность браузера…",
            ForeColor = Color.FromArgb(145, 162, 180),
            Font = new Font("Segoe UI", 9.75f),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(0, 62, 420, 24)
        };
        _statusReference = status;
        _detail.Text = string.Empty;
        _detail.ForeColor = Color.FromArgb(218, 185, 106);
        _detail.Font = new Font("Segoe UI", 9f);
        _detail.AutoSize = false;
        _detail.TextAlign = ContentAlignment.MiddleCenter;
        _detail.Bounds = new Rectangle(0, 88, 420, 20);
        _detail.Visible = false;

        var track = new Label
        {
            BackColor = Color.FromArgb(28, 38, 52),
            Bounds = new Rectangle(TrackLeft, 124, TrackWidth, BarHeight),
            AutoSize = false
        };
        _fill.BackColor = Color.FromArgb(54, 215, 196);
        _fill.SetBounds(TrackLeft, 124, 0, BarHeight);
        _fill.Visible = false;
        _runner.BackColor = Color.FromArgb(54, 215, 196);
        _runner.SetBounds(TrackLeft, 123, 34, BarHeight + 2);
        _runner.Visible = true;

        // Временная шкала этапов: проверка → загрузка → установка → запуск.
        _stages.Text = "○ проверка    ○ загрузка    ○ установка    ○ запуск";
        _stages.ForeColor = Color.FromArgb(110, 126, 146);
        _stages.Font = new Font("Segoe UI", 8.5f);
        _stages.AutoSize = false;
        _stages.TextAlign = ContentAlignment.MiddleCenter;
        _stages.Bounds = new Rectangle(0, 148, 420, 18);

        var percentBig = new Label
        {
            Name = "percentBig",
            Text = string.Empty,
            ForeColor = Color.FromArgb(54, 215, 196),
            Font = new Font("Segoe UI Semibold", 11.25f, FontStyle.Bold),
            AutoSize = false,
            TextAlign = ContentAlignment.MiddleCenter,
            Bounds = new Rectangle(0, 166, 420, 24),
            Visible = false
        };
        Controls.Add(percentBig);
        PercentLabel = percentBig;

        var accent = new Label
        {
            BackColor = Color.FromArgb(54, 215, 196),
            Bounds = new Rectangle(196, 196, 28, 3),
            AutoSize = false
        };

        _animation.Interval = 25;
        _animation.Tick += (_, _) => AdvanceRunner();

        SuspendLayout();
        Controls.Add(track);
        Controls.Add(_fill);
        Controls.Add(_runner);
        Controls.Add(brand);
        Controls.Add(status);
        Controls.Add(_detail);
        Controls.Add(_stages);
        Controls.Add(accent);
        ResumeLayout();

        Paint += DrawFrame;
    }

    private Label PercentLabel { get; }

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

    /// <summary>
    /// Определённый прогресс: шкала заполняется, показываются проценты
    /// и деталь (мегабайты/этап). percent &lt; 0 — вернуться к бегунку.
    /// </summary>
    public void SetProgress(int percent, string detail)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetProgress(percent, detail));
            return;
        }
        _percent = percent;
        if (percent >= 0)
        {
            _animation.Stop();
            _runner.Visible = false;
            _fill.Visible = true;
            _fill.Width = Math.Max(0, Math.Min(100, percent)) * TrackWidth / 100;
            _detail.Text = detail;
            _detail.Visible = detail.Length > 0;
            PercentLabel.Text = percent + "%";
            PercentLabel.Visible = true;
        }
        else
        {
            _fill.Visible = false;
            _fill.Width = 0;
            _runner.Visible = true;
            _detail.Visible = false;
            PercentLabel.Visible = false;
            _animation.Start();
        }
    }

    /// <summary>Подсвечивает этап временной шкалы (1–4).</summary>
    public void SetStage(int stage)
    {
        if (IsDisposed) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => SetStage(stage));
            return;
        }
        string[] stageNames = ["проверка", "загрузка", "установка", "запуск"];
        var marks = new[] { "○", "○", "○", "○" };
        for (var i = 0; i < marks.Length; i++)
            marks[i] = i + 1 < stage ? "●" : i + 1 == stage ? "◉" : "○";
        _stages.Text = string.Join("    ", marks.Zip(stageNames, (mark, name) => mark + " " + name));
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
        else if (_runnerPosition >= TrackWidth - 34)
        {
            _runnerPosition = TrackWidth - 34;
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
