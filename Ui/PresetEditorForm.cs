namespace PodBridge.Ui;

/// <summary>Düşük, Dengeli ve Kararlı ön ayarlarının tampon sürelerini düzenler.</summary>
public sealed class PresetEditorForm : Form
{
    private const int CrackleRiskBelowMs = 100;

    private readonly NumericUpDown _low;
    private readonly NumericUpDown _balanced;
    private readonly NumericUpDown _stable;
    private readonly Label _lowEstimate = new();
    private readonly Label _balancedEstimate = new();
    private readonly Label _stableEstimate = new();
    private readonly Label _warning = new();

    public int LowMs => (int)_low.Value;
    public int BalancedMs => (int)_balanced.Value;
    public int StableMs => (int)_stable.Value;

    public PresetEditorForm(Settings settings)
    {
        Text = "Gecikme ön ayarları";
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
            Text = "Tampon küçüldükçe gecikme azalır, ama Wi-Fi dalgalanmasında ses bozulabilir.",
            AutoSize = true,
            MaximumSize = new Size(360, 0),
            Margin = new Padding(0, 0, 0, 10),
        };
        layout.Controls.Add(intro, 0, 0);
        layout.SetColumnSpan(intro, 3);

        _low = AddRow(layout, 1, "Düşük", settings.LowBufferMs, _lowEstimate);
        _balanced = AddRow(layout, 2, "Dengeli", settings.BalancedBufferMs, _balancedEstimate);
        _stable = AddRow(layout, 3, "Kararlı", settings.StableBufferMs, _stableEstimate);

        _warning.AutoSize = true;
        _warning.MaximumSize = new Size(360, 0);
        _warning.ForeColor = Color.FromArgb(157, 93, 0);
        _warning.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(_warning, 0, 4);
        layout.SetColumnSpan(_warning, 3);

        var reset = new Button { Text = "Varsayılanlar", AutoSize = true };
        reset.Click += (_, _) =>
        {
            _low.Value = Settings.DefaultLowBufferMs;
            _balanced.Value = Settings.DefaultBalancedBufferMs;
            _stable.Value = Settings.DefaultStableBufferMs;
        };
        var save = new Button { Text = "Kaydet", AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Text = "İptal", AutoSize = true, DialogResult = DialogResult.Cancel };
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

        UpdateEstimates();
    }

    private NumericUpDown AddRow(TableLayoutPanel layout, int row, string name, int value, Label estimate)
    {
        layout.Controls.Add(new Label
        {
            Text = name,
            AutoSize = true,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 4, 16, 4),
        }, 0, row);

        var input = new NumericUpDown
        {
            Minimum = Settings.MinBufferMs,
            Maximum = Settings.MaxBufferMs,
            Increment = 10,
            Value = value,
            Width = 80,
            Margin = new Padding(0, 4, 12, 4),
        };
        input.ValueChanged += (_, _) => UpdateEstimates();
        input.TextChanged += (_, _) => UpdateEstimates();
        layout.Controls.Add(input, 1, row);

        estimate.AutoSize = true;
        estimate.Anchor = AnchorStyles.Left;
        layout.Controls.Add(estimate, 2, row);
        return input;
    }

    private void UpdateEstimates()
    {
        // Kurucu sırasında satırlar henüz tamamlanmamış olabilir.
        if (_low == null || _balanced == null || _stable == null) return;
        _lowEstimate.Text = Describe(LowMs);
        _balancedEstimate.Text = Describe(BalancedMs);
        _stableEstimate.Text = Describe(StableMs);
        _warning.Text = Math.Min(LowMs, Math.Min(BalancedMs, StableMs)) < CrackleRiskBelowMs
            ? $"{CrackleRiskBelowMs} ms altında cızırtı riski yüksek. HomePod paketleri 70 ms önceden istiyor; 50 ms'de cızırtı duyuldu."
            : "Gecikme değerleri ölçümlerden tahmindir.";
    }

    private static string Describe(int bufferMs)
    {
        int estimate = Settings.EstimateEndToEndMs(bufferMs);
        return $"ms tampon, gecikme ≈ {(estimate + 5) / 10 * 10} ms";
    }
}
