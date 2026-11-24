using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PodBridge.Ui;

/// <summary>Volume pop-up in the style of the Windows 11 one, shown when the volume or mute keys change the HomePod.
/// Windows cannot be asked to show its own pop-up without also changing (and unmuting) the computer's volume,
/// so PodBridge draws this one. It never takes focus and lets the mouse pass through.</summary>
public sealed class VolumeOsd : Form
{
    private const int BaseWidth = 204;
    private const int BaseHeight = 48;
    private const int BottomGap = 40;
    private const int VisibleMs = 1800;
    private const int FadeSteps = 8;

    private readonly System.Windows.Forms.Timer _hideTimer = new() { Interval = VisibleMs };
    private readonly System.Windows.Forms.Timer _fadeTimer = new() { Interval = 25 };
    private double _percent;
    private bool _muted;
    private float _scale = 1;
    private Color _bg, _border, _text, _track, _accent, _thumbRing;

    public VolumeOsd()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            _fadeTimer.Start();
        };
        _fadeTimer.Tick += (_, _) =>
        {
            Opacity = Math.Max(0, Opacity - 1.0 / FadeSteps);
            if (Opacity > 0) return;
            _fadeTimer.Stop();
            Hide();
        };
        ApplyTheme(null);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT (mouse passes through)
            cp.ExStyle |= 0x00000080 | 0x08000000 | 0x00000008 | 0x00000020;
            cp.ClassStyle |= 0x00020000;   // CS_DROPSHADOW
            return cp;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        int round = 2;   // DWMWCP_ROUND
        DwmSetWindowAttribute(Handle, 33, ref round, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyTheme(bool? lightOverride)
    {
        bool light = lightOverride ?? (Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "SystemUsesLightTheme", 0) is int value && value != 0);
        // The Windows pop-up follows the taskbar (system) theme, not the app theme.
        _bg = light ? Color.FromArgb(249, 249, 249) : Color.FromArgb(44, 44, 44);
        _border = light ? Color.FromArgb(222, 222, 222) : Color.FromArgb(64, 64, 64);
        _text = light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(255, 255, 255);
        _track = light ? Color.FromArgb(138, 138, 138) : Color.FromArgb(158, 158, 158);
        _accent = light ? Color.FromArgb(0, 95, 184) : Color.FromArgb(96, 205, 255);
        _thumbRing = light ? Color.FromArgb(255, 255, 255) : Color.FromArgb(69, 69, 69);
        BackColor = _bg;
    }

    /// <summary>Shows or refreshes the pop-up at the bottom centre of the screen the taskbar is on.</summary>
    public void ShowVolume(double percent, bool muted)
    {
        _percent = Math.Clamp(percent, 0, 100);
        _muted = muted;
        if (!Visible) ApplyTheme(null);
        _scale = DeviceDpi / 96f;
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        int width = (int)(BaseWidth * _scale), height = (int)(BaseHeight * _scale);
        SetBounds(area.Left + (area.Width - width) / 2, area.Bottom - height - (int)(BottomGap * _scale), width, height);
        _fadeTimer.Stop();
        Opacity = 1;
        if (!Visible) Show();
        Invalidate();
        _hideTimer.Stop();
        _hideTimer.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Draw(e.Graphics, ClientSize);
    }

    private float S(float v) => v * _scale;

    private void Draw(Graphics g, Size size)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        g.Clear(_bg);
        using (var pen = new Pen(_border))
            g.DrawRectangle(pen, 0, 0, size.Width - 1, size.Height - 1);

        float mid = size.Height / 2f;

        // HomePod glyph instead of the speaker, so it is clear which device the keys control.
        var icon = new RectangleF(S(16), mid - S(10), S(20), S(20));
        Glyphs.DrawHomePod(g, _text, icon);
        if (_muted)
        {
            using var slash = new Pen(_text, S(1.6f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            using var gap = new Pen(_bg, S(4.5f)) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawLine(gap, icon.Left - S(1), icon.Top - S(1), icon.Right + S(1), icon.Bottom + S(1));
            g.DrawLine(slash, icon.Left, icon.Top, icon.Right, icon.Bottom);
        }

        // Value on the right, slider in between, as in the Windows pop-up.
        using var font = new Font("Segoe UI", S(13), GraphicsUnit.Pixel);
        string label = $"{_percent:F0}";
        var labelSize = g.MeasureString(label, font);
        float labelRight = size.Width - S(16);
        using (var brush = new SolidBrush(_text))
            g.DrawString(label, font, brush, labelRight - labelSize.Width, mid - labelSize.Height / 2);

        float left = S(50), right = labelRight - S(30);
        float trackHeight = S(4);
        var track = new RectangleF(left, mid - trackHeight / 2, right - left, trackHeight);
        float x = left + (right - left) * (float)(_percent / 100.0);
        FillRound(g, _track, track, trackHeight / 2);
        FillRound(g, _muted ? _track : _accent, new RectangleF(left, track.Y, x - left, trackHeight), trackHeight / 2);

        float outer = S(20), inner = S(12);
        using (var ring = new SolidBrush(_thumbRing))
            g.FillEllipse(ring, x - outer / 2, mid - outer / 2, outer, outer);
        using (var pen = new Pen(_border))
            g.DrawEllipse(pen, x - outer / 2, mid - outer / 2, outer, outer);
        using (var dot = new SolidBrush(_muted ? _track : _accent))
            g.FillEllipse(dot, x - inner / 2, mid - inner / 2, inner, inner);
    }

    private static void FillRound(Graphics g, Color color, RectangleF rect, float radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(rect.Width, rect.Height));
        path.AddArc(rect.X, rect.Y, d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
        path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    /// <summary>Testing: draws the pop-up into an image without showing it.</summary>
    public static void RenderTo(string path, double percent, bool muted, bool light, float scale)
    {
        using var osd = new VolumeOsd();
        osd.ApplyTheme(light);
        osd._percent = percent;
        osd._muted = muted;
        osd._scale = scale;
        var size = new Size((int)(BaseWidth * scale), (int)(BaseHeight * scale));
        using var bitmap = new Bitmap(size.Width, size.Height);
        using (var g = Graphics.FromImage(bitmap)) osd.Draw(g, size);
        bitmap.Save(path);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hideTimer.Dispose();
            _fadeTimer.Dispose();
        }
        base.Dispose(disposing);
    }
}
