using System.Drawing.Drawing2D;

namespace PodBridge.Ui;

/// <summary>Edits the buffer length of the Low, Balanced and Stable presets. Drawn in the Windows 11 style
/// (rounded buttons and fields, title bar in the window's colors), light or dark with the Windows app theme.</summary>
public sealed class PresetEditorForm : Form
{
    private const int CrackleRiskBelowMs = 100;

    private readonly MillisecondBox _low;
    private readonly MillisecondBox _balanced;
    private readonly MillisecondBox _stable;
    private readonly Label _lowEstimate = new();
    private readonly Label _balancedEstimate = new();
    private readonly Label _stableEstimate = new();
    private readonly Label _warning = new();
    private readonly Palette _palette = Theme.Current;

    public int LowMs => _low.Value;
    public int BalancedMs => _balanced.Value;
    public int StableMs => _stable.Value;

    public PresetEditorForm(Settings settings)
    {
        Text = L.T("Latency presets", "Gecikme ön ayarları");
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96, 96);
        Font = new Font("Segoe UI", 9.5f);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = _palette.Background;
        ForeColor = _palette.Text;

        var layout = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(20, 16, 20, 16),
            BackColor = _palette.Background,
        };
        Controls.Add(layout);

        var intro = new Label
        {
            Text = L.T("A smaller buffer means less latency, but audio may break up when Wi-Fi is jittery.", "Tampon küçüldükçe gecikme azalır, ama Wi-Fi dalgalanmasında ses bozulabilir."),
            AutoSize = true,
            MaximumSize = new Size(380, 0),
            Margin = new Padding(0, 0, 0, 12),
            ForeColor = _palette.Subtext,
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 3);

        _low = AddRow(layout, 1, L.T("Low", "Düşük"), settings.LowBufferMs, _lowEstimate);
        _balanced = AddRow(layout, 2, L.T("Balanced", "Dengeli"), settings.BalancedBufferMs, _balancedEstimate);
        _stable = AddRow(layout, 3, L.T("Stable", "Kararlı"), settings.StableBufferMs, _stableEstimate);

        _warning.AutoSize = true;
        _warning.MaximumSize = new Size(380, 0);
        _warning.ForeColor = _palette.Warn;
        _warning.Margin = new Padding(0, 10, 0, 14);
        layout.Controls.Add(_warning, 0, 4);
        layout.SetColumnSpan(_warning, 3);

        var reset = new ModernButton(_palette, primary: false) { Text = L.T("Defaults", "Varsayılanlar") };
        reset.Click += (_, _) =>
        {
            _low.Value = Settings.DefaultLowBufferMs;
            _balanced.Value = Settings.DefaultBalancedBufferMs;
            _stable.Value = Settings.DefaultStableBufferMs;
        };
        var save = new ModernButton(_palette, primary: true) { Text = L.T("Save", "Kaydet"), DialogResult = DialogResult.OK };
        var cancel = new ModernButton(_palette, primary: false) { Text = L.T("Cancel", "İptal"), DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
            BackColor = _palette.Background,
        };
        buttons.Controls.AddRange([cancel, save, reset]);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 3);
        AcceptButton = save;
        CancelButton = cancel;

        UpdateEstimates();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.StyleTitleBar(Handle, _palette);
    }

    /// <summary>Windows 11 button: rounded, accent filled when primary, a subtle surface with a border otherwise.</summary>
    private sealed class ModernButton : Button
    {
        private readonly Palette _p;
        private readonly bool _primary;
        private bool _hover, _pressed;

        public ModernButton(Palette palette, bool primary)
        {
            _p = palette;
            _primary = primary;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.SupportsTransparentBackColor, true);
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            AutoSize = true;
            Padding = new Padding(14, 4, 14, 4);
            MinimumSize = new Size(84, 32);
            Margin = new Padding(8, 0, 0, 0);
            Cursor = Cursors.Hand;
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _pressed = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Parent?.BackColor ?? _p.Background);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            Color fill = _primary
                ? _pressed ? _p.AccentPressed : _hover ? _p.AccentHover : _p.Accent
                : _pressed ? _p.SurfacePressed : _hover ? _p.SurfaceHover : _p.Surface;
            using (var path = Theme.RoundRect(rect, 4))
            {
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);
                if (!_primary)
                {
                    using var pen = new Pen(_p.Border);
                    g.DrawPath(pen, path);
                }
            }
            if (Focused && ShowFocusCues)
            {
                using var ring = Theme.RoundRect(RectangleF.Inflate(rect, -2, -2), 3);
                using var pen = new Pen(_p.Text, 1.5f);
                g.DrawPath(pen, ring);
            }
            TextRenderer.DrawText(g, Text, Font, ClientRectangle, _primary ? _p.AccentText : _p.Text,
                                  TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Windows 11 text field: rounded box with a thin border; the bottom edge turns accent while typing.</summary>
    private sealed class FieldBox : Panel
    {
        private readonly Palette _p;
        private readonly TextBox _box;

        public FieldBox(Palette palette, TextBox box)
        {
            _p = palette;
            _box = box;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw, true);
            Size = new Size(78, 32);
            Margin = new Padding(0, 4, 10, 4);
            Padding = new Padding(10, 7, 10, 5);
            BackColor = palette.Background;
            box.BorderStyle = BorderStyle.None;
            box.BackColor = palette.Surface;
            box.ForeColor = palette.Text;
            box.Dock = DockStyle.Fill;
            box.GotFocus += (_, _) => Invalidate();
            box.LostFocus += (_, _) => Invalidate();
            Controls.Add(box);
            Cursor = Cursors.IBeam;
            Click += (_, _) => box.Focus();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(_p.Background);
            var rect = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
            using var path = Theme.RoundRect(rect, 4);
            using (var brush = new SolidBrush(_p.Surface)) g.FillPath(brush, path);
            using (var pen = new Pen(_p.Border)) g.DrawPath(pen, path);
            if (_box.Focused)
            {
                // Accent underline, clipped to the rounded box like the Windows 11 fields.
                g.SetClip(path);
                using var accent = new SolidBrush(_p.Accent);
                g.FillRectangle(accent, 0, Height - 2, Width, 2);
                g.ResetClip();
            }
        }
    }

    /// <summary>Number field. Up/Down arrows change it by 10 ms; invalid input is clamped to the limits.
    /// NumericUpDown is not used because it ignores the background color in dark mode.</summary>
    private sealed class MillisecondBox : TextBox
    {
        private const int Step = 10;

        public MillisecondBox(int value)
        {
            TextAlign = HorizontalAlignment.Right;
            Value = value;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public int Value
        {
            get => int.TryParse(Text, out int v) ? Math.Clamp(v, Settings.MinBufferMs, Settings.MaxBufferMs) : Settings.MinBufferMs;
            set => Text = Math.Clamp(value, Settings.MinBufferMs, Settings.MaxBufferMs).ToString();
        }

        protected override void OnKeyPress(KeyPressEventArgs e)
        {
            if (!char.IsDigit(e.KeyChar) && !char.IsControl(e.KeyChar)) e.Handled = true;
            base.OnKeyPress(e);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode is Keys.Up or Keys.Down)
            {
                Value += e.KeyCode == Keys.Up ? Step : -Step;
                SelectionStart = Text.Length;
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnLeave(EventArgs e)
        {
            Value = Value;
            base.OnLeave(e);
        }
    }

    private MillisecondBox AddRow(TableLayoutPanel layout, int row, string name, int value, Label estimate)
    {
        layout.Controls.Add(new Label
        {
            Text = name,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 18, 4),
            ForeColor = _palette.Text,
        }, 0, row);

        var input = new MillisecondBox(value);
        input.TextChanged += (_, _) => UpdateEstimates();
        layout.Controls.Add(new FieldBox(_palette, input), 1, row);

        estimate.AutoSize = true;
        estimate.Anchor = AnchorStyles.Left;
        estimate.ForeColor = _palette.Subtext;
        layout.Controls.Add(estimate, 2, row);
        return input;
    }

    private void UpdateEstimates()
    {
        // Rows may not exist yet while the constructor runs.
        if (_low == null || _balanced == null || _stable == null) return;
        _lowEstimate.Text = Describe(LowMs);
        _balancedEstimate.Text = Describe(BalancedMs);
        _stableEstimate.Text = Describe(StableMs);
        _warning.Text = Math.Min(LowMs, Math.Min(BalancedMs, StableMs)) < CrackleRiskBelowMs
            ? L.T($"Crackling is likely below {CrackleRiskBelowMs} ms. The HomePod wants packets 70 ms ahead; crackling was heard at 50 ms.", $"{CrackleRiskBelowMs} ms altında cızırtı riski yüksek. HomePod paketleri 70 ms önceden istiyor; 50 ms'de cızırtı duyuldu.")
            : L.T("Latency values are estimates based on measurements.", "Gecikme değerleri ölçümlerden tahmindir.");
    }

    private static string Describe(int bufferMs)
    {
        int estimate = Settings.EstimateEndToEndMs(bufferMs);
        return L.T($"ms buffer, latency ≈ {(estimate + 5) / 10 * 10} ms", $"ms tampon, gecikme ≈ {(estimate + 5) / 10 * 10} ms");
    }

    /// <summary>Testing: draws the dialog's content into an image without showing it on screen.</summary>
    public static void RenderTo(string path, Settings settings)
    {
        using var editor = new PresetEditorForm(settings) { StartPosition = FormStartPosition.Manual, Location = new Point(-4000, -4000), ShowInTaskbar = false };
        editor.CreateControl();
        editor.PerformLayout();
        var size = editor.ClientSize;
        using var bitmap = new Bitmap(size.Width, size.Height);
        editor.DrawToBitmap(bitmap, new Rectangle(Point.Empty, size));
        bitmap.Save(path);
    }
}
