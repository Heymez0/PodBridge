namespace PodBridge.Ui;

/// <summary>Edits the buffer length of the Low, Balanced and Stable presets.</summary>
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

        var layout = new TableLayoutPanel
        {
            ColumnCount = 3,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(14),
        };
        Controls.Add(layout);

        var intro = new Label
        {
            Text = L.T("A smaller buffer means less latency, but audio may break up when Wi-Fi is jittery.", "Tampon küçüldükçe gecikme azalır, ama Wi-Fi dalgalanmasında ses bozulabilir."),
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 3);

        _low = AddRow(layout, 1, L.T("Low", "Düşük"), settings.LowBufferMs, _lowEstimate);
        _balanced = AddRow(layout, 2, L.T("Balanced", "Dengeli"), settings.BalancedBufferMs, _balancedEstimate);
        _stable = AddRow(layout, 3, L.T("Stable", "Kararlı"), settings.StableBufferMs, _stableEstimate);

        _warning.AutoSize = true;
        _warning.MaximumSize = new Size(360, 0);
        _warning.ForeColor = Color.FromArgb(157, 93, 0);
        _warning.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(_warning, 0, 4);
        layout.SetColumnSpan(_warning, 3);

        var reset = new Button { Text = L.T("Defaults", "Varsayılanlar"), AutoSize = true };
        reset.Click += (_, _) =>
        {
            _low.Value = Settings.DefaultLowBufferMs;
            _balanced.Value = Settings.DefaultBalancedBufferMs;
            _stable.Value = Settings.DefaultStableBufferMs;
        };
        var save = new Button { Text = L.T("Save", "Kaydet"), AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = L.T("Cancel", "İptal"), AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0),
        };
        buttons.Controls.AddRange([cancel, save, reset]);
        layout.Controls.Add(buttons, 0, 5);
        layout.SetColumnSpan(buttons, 3);
        AcceptButton = save;
        CancelButton = cancel;

        ApplyTheme(Glyphs.AppsUseLightTheme);
        UpdateEstimates();
    }

    private readonly bool _dark = !Glyphs.AppsUseLightTheme;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar in dark mode (DWMWA_USE_IMMERSIVE_DARK_MODE).
        int dark = _dark ? 1 : 0;
        DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Light or dark look using the same colors as the panel.</summary>
    private void ApplyTheme(bool light)
    {
        var background = light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32);
        var surface = light ? Color.FromArgb(251, 251, 251) : Color.FromArgb(45, 45, 45);
        var text = light ? Color.FromArgb(26, 26, 26) : Color.White;
        var border = light ? Color.FromArgb(214, 214, 214) : Color.FromArgb(70, 70, 70);
        var accent = light ? Color.FromArgb(0, 103, 192) : Color.FromArgb(96, 205, 255);
        var accentText = light ? Color.White : Color.FromArgb(0, 30, 50);

        BackColor = background;
        ForeColor = text;
        foreach (var control in Descendants(this))
        {
            switch (control)
            {
                case MillisecondBox input:
                    input.BackColor = surface;
                    input.ForeColor = text;
                    break;
                case Button button:
                    bool primary = button.DialogResult == DialogResult.OK;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = primary ? accent : border;
                    button.BackColor = primary ? accent : surface;
                    button.ForeColor = primary ? accentText : text;
                    button.Padding = new Padding(8, 2, 8, 2);
                    break;
                default:
                    control.BackColor = background;
                    control.ForeColor = text;
                    break;
            }
        }
        _warning.ForeColor = light ? Color.FromArgb(157, 93, 0) : Color.FromArgb(255, 200, 80);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    /// <summary>Themed number box. Up/Down arrows change it by 10 ms; invalid input is clamped to the limits.
    /// NumericUpDown is not used because it ignores the background color in dark mode.</summary>
    private sealed class MillisecondBox : TextBox
    {
        private const int Step = 10;

        public MillisecondBox(int value)
        {
            BorderStyle = BorderStyle.FixedSingle;
            TextAlign = HorizontalAlignment.Right;
            Width = 70;
            Margin = new Padding(0, 4, 8, 4);
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
            Margin = new Padding(0, 4, 16, 4),
        }, 0, row);

        var input = new MillisecondBox(value);
        input.TextChanged += (_, _) => UpdateEstimates();
        layout.Controls.Add(input, 1, row);

        estimate.AutoSize = true;
        estimate.Anchor = AnchorStyles.Left;
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
}
