using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace Nexus.Guardian;

/// <summary>
/// Круглый пульсирующий сплэш Guardian — раннее стартовое окно. Пока браузер
/// ещё не проверен и не запущен, именно это окно тем же языком, что и круглый
/// сплэш браузера, показывает работу Guardian: кольцо, разделённое на секторы
/// по процессам (целостность → обновление → загрузка → запуск), с полосой
/// заполнения в активном секторе. Прямоугольного окна больше нет.
/// Отрисовка через UpdateLayeredWindow — попиксельная прозрачность.
/// </summary>
internal sealed class GuardianSplash : Form
{
    private const int LogicalWidth = 360;
    private const int LogicalHeight = 400;
    private const float CenterX = 180f;
    private const float CenterY = 200f;
    private const float GlowRadius = 161f;
    private const float DiscRadius = 147f;
    private const float RingRadius = 157f;
    private const float RingStroke = 6f;
    private const float SectorGapDeg = 6f;
    private const float SectorSweepDeg = 90f - SectorGapDeg;
    private const int SectorCount = 4;
    private static readonly string[] SectorNames = ["целостность", "обновление", "загрузка", "запуск"];

    private static readonly Color GlowTint = Color.FromArgb(54, 215, 196);
    private static readonly Color DiscFill = Color.FromArgb(0xD9, 0x0B, 0x10, 0x18);
    private static readonly Color DiscStroke = Color.FromArgb(0x55, 54, 215, 196);
    private static readonly Color TrackColor = Color.FromArgb(0x2D, 0x26, 0x34);
    private static readonly Color Aqua = Color.FromArgb(54, 215, 196);
    private static readonly Color DoneColor = Color.FromArgb(0x73, 54, 215, 196);
    private static readonly Color BrandColor = Color.FromArgb(0xEE, 0xF4, 0xF8);
    private static readonly Color StatusColor = Color.FromArgb(0x91, 0xA2, 0xB4);
    private static readonly Color DetailColor = Color.FromArgb(0xDA, 0xB9, 0x6A);
    private static readonly Color StagesColor = Color.FromArgb(0x6E, 0x7E, 0x92);

    private const int ULW_ALPHA = 2;
    private const byte AC_SRC_OVER = 0;
    private const byte AC_SRC_ALPHA = 1;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT(int x, int y)
    {
        public int X = x;
        public int Y = y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE(int width, int height)
    {
        public int Width = width;
        public int Height = height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst, ref SIZE psize,
        IntPtr hdcSrc, ref POINT pprSrc, int crKey, ref BLENDFUNCTION pBlend, int dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    private readonly float _scale;
    private readonly Bitmap _surface;
    private readonly Graphics _canvas;
    private readonly Image? _emblem;
    private readonly Func<int>? _integrityPercent;
    private readonly System.Windows.Forms.Timer _animation = new();
    private readonly long _started = Stopwatch.GetTimestamp();

    // Сектор: -2 ждёт очереди, -1 неопределённый ход (комета), 0–99 заполнение, 100 готов.
    private readonly int[] _sectorState = [-1, -2, -2, -2];
    private double _cometDeg;
    private string _status = "Guardian проверяет целостность…";
    private string _detail = string.Empty;
    private bool _closed;

    public GuardianSplash(string applicationRoot, Func<int>? integrityPercent = null)
    {
        _integrityPercent = integrityPercent;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = false;
        TopMost = true;
        using (var probe = CreateGraphics())
        {
            _scale = probe.DpiX / 96f;
        }
        ClientSize = new Size((int)(LogicalWidth * _scale), (int)(LogicalHeight * _scale));
        _surface = new Bitmap(ClientSize.Width, ClientSize.Height, PixelFormat.Format32bppArgb);
        _canvas = Graphics.FromImage(_surface);
        _canvas.SmoothingMode = SmoothingMode.AntiAlias;
        _canvas.TextRenderingHint = TextRenderingHint.AntiAlias;
        _emblem = LoadEmblem(applicationRoot);

        _animation.Interval = 33;
        _animation.Tick += (_, _) => RenderFrame();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE;
            return parameters;
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        RenderFrame();
        _animation.Start();
    }

    /// <summary>Статусная строка под названием.</summary>
    public void SetStatus(string text) => _status = text;

    /// <summary>Золотая строка деталей: мегабайты, версия, пояснение.</summary>
    public void SetDetail(string text) => _detail = text;

    /// <summary>Активировать сектор: -1 — неопределённый ход, 0–100 — заполнение.</summary>
    public void ActivateSector(int index, int percent)
    {
        if (index is < 0 or >= SectorCount) return;
        _sectorState[index] = Math.Clamp(percent, -1, 100);
    }

    /// <summary>Сектор завершён: полная мягкая заливка.</summary>
    public void CompleteSector(int index)
    {
        if (index is < 0 or >= SectorCount) return;
        _sectorState[index] = 100;
    }

    /// <summary>Ход обновления из StartupUpdate — на секторы и строки окна.</summary>
    public void ShowUpdateProgress(SilentUpdateCoordinator.UpdateProgress update)
    {
        // Колбэк приходит из threadpool-задачи проверки обновления.
        if (IsHandleCreated && InvokeRequired)
        {
            BeginInvoke(() => ShowUpdateProgress(update));
            return;
        }
        SetStatus(update.Stage);
        SetDetail(update.Detail);
        if (update.Stage.StartsWith("Проверяю обновления", StringComparison.Ordinal))
        {
            CompleteSector(0);
            ActivateSector(1, update.Percent);
        }
        else if (update.Stage.StartsWith("Найдена", StringComparison.Ordinal) ||
                 update.Stage.StartsWith("Скачиваю", StringComparison.Ordinal))
        {
            CompleteSector(0);
            CompleteSector(1);
            ActivateSector(2, update.Percent);
        }
        else if (update.Stage.StartsWith("Проверяю подпись", StringComparison.Ordinal))
        {
            CompleteSector(0);
            CompleteSector(1);
            CompleteSector(2);
        }
        else if (update.Stage.StartsWith("Устанавливаю", StringComparison.Ordinal))
        {
            CompleteSector(0);
            CompleteSector(1);
            CompleteSector(2);
            ActivateSector(3, -1);
        }
        else if (update.Stage.StartsWith("Версия актуальна", StringComparison.Ordinal))
        {
            CompleteSector(0);
            CompleteSector(1);
        }
    }

    /// <summary>Мягкое закрытие: окно исчезает, эстафета у сплэша браузера.</summary>
    public void CloseGraceful()
    {
        if (_closed) return;
        _closed = true;
        _animation.Stop();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _animation.Dispose();
            _canvas.Dispose();
            _surface.Dispose();
            _emblem?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static Image? LoadEmblem(string applicationRoot)
    {
        try
        {
            var path = Path.Combine(applicationRoot, "Assets", "nexus-monach-512.png");
            return File.Exists(path) ? Image.FromFile(path) : null;
        }
        catch { /* эмблема желательна, но не обязательна */ }
        return null;
    }

    private void RenderFrame()
    {
        if (_closed) return;
        // Проценты хеширования приходят из параллельного потока — читаем
        // каждый кадр, рисует только поток интерфейса.
        if (_integrityPercent is { } provider && _sectorState[0] >= 0 && _sectorState[0] < 100)
        {
            var percent = provider();
            if (percent >= 0)
            {
                _sectorState[0] = Math.Clamp(percent, 0, 99);
                _detail = "файлов проверено " + percent + "%";
            }
        }

        var elapsedMs = (double)(Stopwatch.GetTimestamp() - _started) / Stopwatch.Frequency * 1000.0;
        var breathe = 0.5 - 0.5 * Math.Cos(elapsedMs / 1400.0 * Math.PI * 2);
        _cometDeg = (elapsedMs / 1500.0 * 360.0) % 360;

        _canvas.Clear(Color.Transparent);
        var s = _scale;

        // Дыхание: светящийся диск за эмблемой, как у сплэша браузера.
        using (var glowBrush = new SolidBrush(Color.FromArgb((int)(20 + breathe * 38), GlowTint)))
        {
            FillCircle(glowBrush, CenterX, CenterY, GlowRadius);
        }
        using (var discBrush = new SolidBrush(DiscFill))
        {
            FillCircle(discBrush, CenterX, CenterY, DiscRadius);
        }
        using (var discPen = new Pen(DiscStroke, 2 * s))
        {
            DrawCircle(discPen, CenterX, CenterY, DiscRadius);
        }

        DrawSectorRing();

        if (_emblem is not null)
        {
            var logoSize = 150 * s * (0.97f + 0.05f * (float)breathe);
            var logoCenterX = CenterX * s;
            var logoCenterY = (78 + 75) * s;
            _canvas.DrawImage(_emblem,
                (float)(logoCenterX - logoSize / 2), (float)(logoCenterY - logoSize / 2),
                (float)logoSize, (float)logoSize);
        }

        DrawTexts();

        PushSurface();
    }

    private void DrawSectorRing()
    {
        var s = _scale;
        using var trackPen = new Pen(TrackColor, RingStroke * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (var i = 0; i < SectorCount; i++)
        {
            var start = i * 90f + SectorGapDeg / 2f;
            DrawArc(trackPen, start, SectorSweepDeg);
        }

        using var fillPen = new Pen(Aqua, RingStroke * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var donePen = new Pen(DoneColor, RingStroke * s) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        for (var i = 0; i < SectorCount; i++)
        {
            var start = i * 90f + SectorGapDeg / 2f;
            var state = _sectorState[i];
            if (state == 100)
                DrawArc(donePen, start, SectorSweepDeg);
            else if (state >= 0)
                DrawArc(fillPen, start, SectorSweepDeg * state / 100f);
            else if (state == -1)
            {
                // Неопределённый ход: комета в 26° бежит по сектору туда-обратно.
                var span = SectorSweepDeg - 26f;
                var ping = 0.5 - 0.5 * Math.Cos(_cometDeg / 360.0 * Math.PI * 2);
                var cometStart = start + span * ping;
                DrawArc(fillPen, (float)cometStart, 26f);
            }
        }
    }

    private void DrawTexts()
    {
        var s = _scale;
        var width = ClientSize.Width;
        using var brandFont = new Font("Segoe UI Semibold", 19 * s, FontStyle.Bold, GraphicsUnit.Pixel);
        using var statusFont = new Font("Segoe UI", 11 * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var detailFont = new Font("Segoe UI", 10 * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var stagesFont = new Font("Segoe UI", 8.5f * s, FontStyle.Regular, GraphicsUnit.Pixel);
        using var center = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            Trimming = StringTrimming.EllipsisCharacter,
            FormatFlags = StringFormatFlags.NoWrap
        };

        DrawLabel(brandFont, BrandColor, _textBrand, 240, 28);
        DrawLabel(statusFont, StatusColor, _status, 272, 18);
        DrawLabel(detailFont, DetailColor, _detail, 292, 14);
        DrawLabel(stagesFont, StagesColor, StagesRow(), 310, 14);
        return;

        void DrawLabel(Font font, Color color, string text, int topLogical, int heightLogical)
        {
            if (string.IsNullOrEmpty(text)) return;
            using var brush = new SolidBrush(color);
            var bounds = new RectangleF(0, topLogical * s, width, heightLogical * s);
            _canvas.DrawString(text, font, brush, bounds, center);
        }
    }

    private static readonly string _textBrand = "NEXUS MONACH";

    private string StagesRow()
    {
        var parts = new string[SectorCount];
        for (var i = 0; i < SectorCount; i++)
            parts[i] = (_sectorState[i] == 100 ? "●" : _sectorState[i] != -2 ? "◉" : "○") + " " + SectorNames[i];
        return string.Join("   ", parts);
    }

    private void FillCircle(Brush brush, float centerX, float centerY, float radiusLogical)
    {
        var s = _scale;
        _canvas.FillEllipse(brush, (centerX - radiusLogical) * s, (centerY - radiusLogical) * s,
            radiusLogical * 2 * s, radiusLogical * 2 * s);
    }

    private void DrawCircle(Pen pen, float centerX, float centerY, float radiusLogical)
    {
        var s = _scale;
        _canvas.DrawEllipse(pen, (centerX - radiusLogical) * s, (centerY - radiusLogical) * s,
            radiusLogical * 2 * s, radiusLogical * 2 * s);
    }

    /// <summary>Дуга сектора: углы от 12 часов по часовой стрелке.</summary>
    private void DrawArc(Pen pen, float startFromTopDeg, float sweepDeg)
    {
        var s = _scale;
        var bounds = new RectangleF((CenterX - RingRadius) * s, (CenterY - RingRadius) * s,
            RingRadius * 2 * s, RingRadius * 2 * s);
        _canvas.DrawArc(pen, bounds, startFromTopDeg - 90f, sweepDeg);
    }

    private void PushSurface()
    {
        var screenDc = GetDC(IntPtr.Zero);
        var memoryDc = CreateCompatibleDC(screenDc);
        var surfaceBitmap = _surface.GetHbitmap(Color.FromArgb(0));
        var previousBitmap = IntPtr.Zero;
        try
        {
            previousBitmap = SelectObject(memoryDc, surfaceBitmap);
            var size = new SIZE(_surface.Width, _surface.Height);
            var source = new POINT(0, 0);
            var position = new POINT(Left, Top);
            var blend = new BLENDFUNCTION
            {
                BlendOp = AC_SRC_OVER,
                BlendFlags = 0,
                SourceConstantAlpha = 255,
                AlphaFormat = AC_SRC_ALPHA
            };
            UpdateLayeredWindow(Handle, screenDc, ref position, ref size, memoryDc, ref source,
                0, ref blend, ULW_ALPHA);
        }
        finally
        {
            if (previousBitmap != IntPtr.Zero) SelectObject(memoryDc, previousBitmap);
            DeleteObject(surfaceBitmap);
            DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
