using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PodBridge.Protocol;

namespace PodBridge.Ui;

/// <summary>Tray panel styled after the Windows 11 quick settings flyout.
/// Drawn on a single surface; clickable regions are re-registered on every paint.</summary>
public sealed class FlyoutForm : Form
{
    private const int BaseWidth = 340;
    private const int Pad = 16;
    private const int RowHeight = 44;

    private readonly AppController _app;
    private readonly List<(RectangleF Bounds, Action Click, string Key)> _hits = [];
    private RectangleF _sliderTrack;
    private bool _draggingSlider;
    private double _sliderValue = -1;
    private string? _hoverKey;
    private float _scale = 1;

    private Color _bg, _card, _cardHover, _text, _subtext, _accent, _accentText, _line, _warn;

    public FlyoutForm(AppController app)
    {
        _app = app;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        KeyPreview = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Deactivate += (_, _) => { if (!_draggingSlider) Hide(); };
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Hide(); };
        ApplyTheme();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= 0x00000080;   // WS_EX_TOOLWINDOW: hidden from Alt+Tab
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

    private void ApplyTheme()
    {
        bool light = Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
            "AppsUseLightTheme", 1) is int value && value != 0;
        _bg = light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
        _card = light ? Color.FromArgb(251, 251, 251) : Color.FromArgb(45, 45, 45);
        _cardHover = light ? Color.FromArgb(240, 240, 240) : Color.FromArgb(56, 56, 56);
        _text = light ? Color.FromArgb(26, 26, 26) : Color.FromArgb(255, 255, 255);
        _subtext = light ? Color.FromArgb(96, 96, 96) : Color.FromArgb(170, 170, 170);
        _accent = light ? Color.FromArgb(0, 103, 192) : Color.FromArgb(96, 205, 255);
        _accentText = light ? Color.White : Color.FromArgb(0, 30, 50);
        _line = light ? Color.FromArgb(225, 225, 225) : Color.FromArgb(60, 60, 60);
        _warn = light ? Color.FromArgb(157, 93, 0) : Color.FromArgb(252, 225, 0);
        BackColor = _bg;
    }

    public void ShowNearTray()
    {
        ApplyTheme();
        _scale = DeviceDpi / 96f;
        Refresh();
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        int width = (int)(BaseWidth * _scale);
        int height = MeasureHeight();
        int margin = (int)(12 * _scale);
        SetBounds(area.Right - width - margin, area.Bottom - height - margin, width, height);
        Show();
        Activate();
        _ = _app.RefreshDevicesAsync();
    }

    public void Sync()
    {
        if (!Visible) return;
        int height = MeasureHeight();
        if (height != Height)
        {
            var area = Screen.FromControl(this).WorkingArea;
            SetBounds(Left, area.Bottom - height - (int)(12 * _scale), Width, height);
        }
        Invalidate();
    }

    private float S(float v) => v * _scale;

    private int MeasureHeight()
    {
        float h = Pad + 44;
        h += Math.Max(_app.Devices.Count, 1) * (RowHeight + 4);
        if (_app.Engine.State == BridgeState.NeedsFirewall) h += 72;
        h += 58 + 8 + 70 + 3 * 36 + Pad;
        return (int)S(h);
    }

    // ---------------------------------------------------------------- painting

    protected override void OnPaint(PaintEventArgs e)
    {
        _scale = DeviceDpi / 96f;
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        _hits.Clear();

        using var bigFont = new Font("Segoe UI Semibold", S(15), GraphicsUnit.Pixel);
        using var bodyFont = new Font("Segoe UI", S(14), GraphicsUnit.Pixel);
        using var smallFont = new Font("Segoe UI", S(12), GraphicsUnit.Pixel);

        float x = S(Pad), w = Width - 2 * S(Pad), y = S(Pad);
        var engine = _app.Engine;

        DrawText(g, L.T("Stream to HomePod", "Sesi HomePod'a aktar"), bigFont, _text, x, y, w);
        y += S(24);
        DrawText(g, StatusText(), smallFont, engine.State is BridgeState.NeedsFirewall or BridgeState.Failed ? _warn : _subtext, x, y, w);
        y += S(26);

        if (_app.Devices.Count == 0)
        {
            DrawCard(g, x, y, w, S(RowHeight), false, false);
            DrawText(g, _app.IsBrowsing ? L.T("Looking for HomePods…", "HomePod aranıyor…") : L.T("No HomePod found", "HomePod bulunamadı"), bodyFont, _subtext, x + S(14), y + S(12), w);
            y += S(RowHeight + 4);
        }
        foreach (var device in _app.Devices)
        {
            bool active = engine.Device?.Id == device.Id
                          && engine.State is BridgeState.Streaming or BridgeState.Connecting or BridgeState.Reconnecting;
            string key = "device:" + device.Id;
            var rect = new RectangleF(x, y, w, S(RowHeight));
            _hits.Add((rect, () => _app.ToggleDevice(device), key));
            DrawCard(g, x, y, w, S(RowHeight), active, _hoverKey == key);
            DrawSpeaker(g, x + S(14), y + S(13), active ? _accentText : _text);
            DrawText(g, device.Name, bodyFont, active ? _accentText : _text, x + S(40), y + S(12), w - S(90));
            if (active && engine.State == BridgeState.Streaming)
                DrawText(g, L.T("Connected", "Bağlı"), smallFont, _accentText, x + w - S(46), y + S(14), S(46));
            y += S(RowHeight + 4);
        }

        if (engine.State == BridgeState.NeedsFirewall)
        {
            DrawCard(g, x, y, w, S(66), false, false);
            DrawText(g, L.T("The HomePod's clock packets are blocked. Windows needs a one-time permission.", "HomePod'un saat paketleri engelleniyor. Windows'ta tek seferlik izin gerekli."), smallFont, _text, x + S(12), y + S(8), w - S(24));
            var btn = new RectangleF(x + S(12), y + S(38), S(100), S(22));
            _hits.Add((btn, () => _app.RequestFirewall(), "firewall"));
            Fill(g, _accent, btn, S(4));
            DrawText(g, L.T("Allow", "İzin ver"), smallFont, _accentText, btn.X + S(28), btn.Y + S(2), btn.Width);
            y += S(72);
        }

        y += S(6);
        DrawText(g, L.T("Volume", "Ses seviyesi"), smallFont, _subtext, x, y, w);
        double shown = _sliderValue >= 0 ? _sliderValue : Math.Max(engine.Volume, 0);
        DrawText(g, L.T($"{shown:F0}%", $"%{shown:F0}"), smallFont, _subtext, x + w - S(36), y, S(40));
        y += S(22);
        _sliderTrack = new RectangleF(x + S(4), y + S(8), w - S(8), S(4));
        bool sliderEnabled = engine.State == BridgeState.Streaming;
        Fill(g, _line, _sliderTrack, S(2));
        float knobX = _sliderTrack.X + _sliderTrack.Width * (float)(shown / 100.0);
        Fill(g, sliderEnabled ? _accent : _subtext, new RectangleF(_sliderTrack.X, _sliderTrack.Y, knobX - _sliderTrack.X, _sliderTrack.Height), S(2));
        using (var knob = new SolidBrush(sliderEnabled ? _accent : _subtext))
            g.FillEllipse(knob, knobX - S(7), _sliderTrack.Y + S(2) - S(7), S(14), S(14));
        y += S(36);

        using (var pen = new Pen(_line)) g.DrawLine(pen, x, y, x + w, y);
        y += S(8);

        DrawText(g, L.T("Latency", "Gecikme"), smallFont, _subtext, x, y, w);
        var editSize = g.MeasureString(L.T("Edit", "Düzenle"), smallFont);
        var editRect = new RectangleF(x + w - editSize.Width, y, editSize.Width, editSize.Height);
        _hits.Add((editRect, () => _app.EditLatencyPresets(), "edit-presets"));
        DrawText(g, L.T("Edit", "Düzenle"), smallFont, _accent, editRect.X, editRect.Y, editRect.Width + S(4));
        y += S(20);
        string[] labels = [L.T("Low", "Düşük"), L.T("Balanced", "Dengeli"), L.T("Stable", "Kararlı")];
        float segment = w / 3;
        for (int i = 0; i < 3; i++)
        {
            var mode = (LatencyMode)i;
            var rect = new RectangleF(x + i * segment + (i > 0 ? S(2) : 0), y, segment - S(2), S(40));
            string key = "latency:" + i;
            _hits.Add((rect, () => _app.SetLatency(mode), key));
            bool on = _app.Settings.Latency == mode;
            Fill(g, on ? _accent : _hoverKey == key ? _cardHover : _card, rect, S(4));
            string buffer = $"{_app.Settings.BufferMs(mode)} ms";
            var size = g.MeasureString(labels[i], smallFont);
            var bufferSize = g.MeasureString(buffer, smallFont);
            using var brush = new SolidBrush(on ? _accentText : _text);
            g.DrawString(labels[i], smallFont, brush, rect.X + (rect.Width - size.Width) / 2, rect.Y + S(3));
            g.DrawString(buffer, smallFont, brush, rect.X + (rect.Width - bufferSize.Width) / 2, rect.Y + S(20));
        }
        y += S(50);

        DrawCheck(g, L.T("Mute computer speakers", "Bilgisayar hoparlörünü sustur"), _app.Settings.MuteLocal, x, ref y, w, smallFont, "mute", () => _app.ToggleMuteLocal());
        DrawCheck(g, L.T("Start with Windows", "Windows ile başlat"), _app.AutoStart, x, ref y, w, smallFont, "autostart", () => _app.ToggleAutoStart());
        DrawCheck(g, L.T("Connect automatically at startup", "Açılışta otomatik bağlan"), _app.Settings.AutoConnect, x, ref y, w, smallFont, "autoconnect", () => _app.ToggleAutoConnect());
    }

    private string StatusText()
    {
        var engine = _app.Engine;
        return engine.State switch
        {
            BridgeState.Streaming => L.T($"Playing on {engine.Device?.Name} · latency {_app.Settings.LatencyLabel()}", $"{engine.Device?.Name} çalıyor · gecikme {_app.Settings.LatencyLabel()}"),
            BridgeState.Connecting => L.T("Connecting…", "Bağlanıyor…"),
            BridgeState.Reconnecting => L.T($"Connection lost, {engine.Detail}", $"Bağlantı koptu, {engine.Detail}"),
            BridgeState.TakenOver => L.T("The HomePod switched to another device", "HomePod başka bir cihaza geçti"),
            BridgeState.NeedsFirewall => L.T("Firewall permission needed", "Güvenlik duvarı izni gerekiyor"),
            BridgeState.Failed => engine.Detail,
            _ => L.T("Pick a HomePod", "Bir HomePod seç"),
        };
    }

    private void DrawCheck(Graphics g, string label, bool value, float x, ref float y, float w, Font font, string key, Action click)
    {
        var row = new RectangleF(x, y, w, S(30));
        _hits.Add((row, click, key));
        var box = new RectangleF(x, y + S(6), S(18), S(18));
        Fill(g, value ? _accent : _card, box, S(4));
        if (!value)
        {
            using var border = new Pen(_subtext);
            using var path = RoundRect(box, S(4));
            g.DrawPath(border, path);
        }
        else
        {
            using var check = new Pen(_accentText, S(2));
            g.DrawLines(check, new[] { new PointF(x + S(4), y + S(15)), new PointF(x + S(8), y + S(19)), new PointF(x + S(14), y + S(10)) });
        }
        DrawText(g, label, font, _hoverKey == key ? _accent : _text, x + S(26), y + S(6), w - S(26));
        y += S(36);
    }

    private void DrawCard(Graphics g, float x, float y, float w, float h, bool active, bool hover) =>
        Fill(g, active ? _accent : hover ? _cardHover : _card, new RectangleF(x, y, w, h), S(6));

    private static void DrawText(Graphics g, string text, Font font, Color color, float x, float y, float w)
    {
        using var brush = new SolidBrush(color);
        using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter };
        g.DrawString(text, font, brush, new RectangleF(x, y, w, font.Height * 2.2f), format);
    }

    private static void Fill(Graphics g, Color color, RectangleF rect, float radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;
        using var brush = new SolidBrush(color);
        using var path = RoundRect(rect, radius);
        g.FillPath(brush, path);
    }

    private static GraphicsPath RoundRect(RectangleF r, float radius)
    {
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    private void DrawSpeaker(Graphics g, float x, float y, Color color)
    {
        Glyphs.DrawHomePod(g, color, new RectangleF(x - S(2), y - S(2), S(22), S(22)));
    }

    /// <summary>Renders the panel to an image so its look can be checked without showing it.</summary>
    public void RenderTo(string path)
    {
        _scale = DeviceDpi / 96f;
        Size = new Size((int)S(BaseWidth), MeasureHeight());
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
        bitmap.Save(path);
    }

    // ---------------------------------------------------------------- mouse

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_draggingSlider)
        {
            UpdateSlider(e.X);
            return;
        }
        string? key = _hits.FirstOrDefault(h => h.Bounds.Contains(e.Location)).Key;
        if (key != _hoverKey)
        {
            _hoverKey = key;
            Invalidate();
        }
        Cursor = key != null ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        var grab = RectangleF.Inflate(_sliderTrack, 0, S(10));
        if (grab.Contains(e.Location) && _app.Engine.State == BridgeState.Streaming)
        {
            _draggingSlider = true;
            Capture = true;
            UpdateSlider(e.X);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (_draggingSlider)
        {
            _draggingSlider = false;
            Capture = false;
            _sliderValue = -1;
            Invalidate();
            return;
        }
        var hit = _hits.FirstOrDefault(h => h.Bounds.Contains(e.Location));
        hit.Click?.Invoke();
    }

    private void UpdateSlider(int mouseX)
    {
        double value = Math.Clamp((mouseX - _sliderTrack.X) / _sliderTrack.Width * 100.0, 0, 100);
        _sliderValue = value;
        _ = _app.Engine.SetVolumeAsync(value);
        Invalidate();
    }
}
