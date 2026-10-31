using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PodBridge.Audio;
using PodBridge.Protocol;

namespace PodBridge.Ui;

/// <summary>Tepsi simgesi, panel ve motor arasındaki bağ. Windows ileti döngüsünde çalışır.</summary>
public sealed class AppController : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PodBridge";
    private const int HotkeyId = 0x5042;

    private readonly NotifyIcon _tray;
    private readonly FlyoutForm _flyout;
    private readonly HotkeyWindow _hotkeys;
    private readonly System.Windows.Forms.Timer _browseTimer;
    private readonly SynchronizationContext _ui;
    private readonly Icon _idleIcon = DrawIcon(Color.FromArgb(150, 150, 150));
    private readonly Icon _activeIcon = DrawIcon(Color.FromArgb(0, 150, 255));
    private readonly Icon _warnIcon = DrawIcon(Color.FromArgb(240, 170, 0));
    private BridgeState _lastState;
    private AirPlayDevice? _lastDevice;

    public Settings Settings { get; }
    public BridgeEngine Engine { get; }
    public IReadOnlyList<AirPlayDevice> Devices { get; private set; } = [];
    public bool IsBrowsing { get; private set; }

    public AppController(Settings settings)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Settings = settings;
        LocalOutputControl.RestoreAfterCrash(settings);
        Engine = new BridgeEngine(settings);
        Engine.Changed += () => _ui.Post(_ => OnEngineChanged(), null);
        Devices = FilterDevices(settings.KnownDevices);

        _flyout = new FlyoutForm(this);
        _ = _flyout.Handle;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Paneli aç", null, (_, _) => _flyout.ShowNearTray());
        menu.Items.Add("Günlük klasörünü aç", null, (_, _) =>
            System.Diagnostics.Process.Start("explorer.exe", Settings.Folder));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Kaldır…", null, (_, _) => Uninstall());
        menu.Items.Add("Çıkış", null, (_, _) => Quit());

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "PodBridge",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_flyout.Visible) _flyout.Hide();
            else _flyout.ShowNearTray();
        };

        _hotkeys = new HotkeyWindow(ToggleLast);
        _browseTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _browseTimer.Tick += (_, _) => _ = RefreshDevicesAsync();
        _browseTimer.Start();

        FirstRun();
        _ = StartupAsync();
    }

    private static List<AirPlayDevice> FilterDevices(IEnumerable<AirPlayDevice> devices) =>
        [.. devices.Where(d => d.IsHomePod).OrderBy(d => d.Name, StringComparer.CurrentCulture)];

    private void FirstRun()
    {
        if (Settings.FirstRunDone) return;
        Settings.FirstRunDone = true;
        Settings.Save();
        SetAutoStart(true);
        CreateStartMenuShortcut();
        _tray.ShowBalloonTip(5000, "PodBridge hazır",
            "Görev çubuğundaki simgeye tıkla ve HomePod'unu seç. Ctrl+Alt+P ile hızlıca aç/kapat.", ToolTipIcon.Info);
    }

    private async Task StartupAsync()
    {
        await RefreshDevicesAsync();
        if (Settings.AutoConnect && Settings.LastDeviceId is { } id
            && Devices.FirstOrDefault(d => d.Id == id) is { } device)
        {
            Engine.Connect(device);
        }
    }

    public async Task RefreshDevicesAsync()
    {
        if (IsBrowsing) return;
        IsBrowsing = true;
        _flyout.Sync();
        try
        {
            var found = await MdnsBrowser.BrowseAsync(TimeSpan.FromSeconds(1.8));
            var homePods = FilterDevices(found);
            if (homePods.Count > 0)
            {
                // Şu an yanıt vermeyen ama daha önce görülen cihazlar listede kalır.
                var merged = Settings.KnownDevices.Where(k => homePods.All(d => d.Id != k.Id)).Concat(homePods);
                Settings.KnownDevices = FilterDevices(merged);
                Settings.Save();
                Devices = Settings.KnownDevices;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Cihaz arama: {ex.Message}");
        }
        IsBrowsing = false;
        _flyout.Sync();
    }

    // ---------------------------------------------------------------- eylemler

    private bool IsActive(AirPlayDevice device) =>
        Engine.Device?.Id == device.Id
        && Engine.State is BridgeState.Streaming or BridgeState.Connecting or BridgeState.Reconnecting;

    public void ToggleDevice(AirPlayDevice device)
    {
        if (IsActive(device))
        {
            _ = Engine.DisconnectAsync();
            return;
        }
        if (!FirewallRule.Exists())
        {
            Log.Write("Güvenlik duvarı kuralı yok, izin isteniyor");
            if (!RequestFirewallNow()) return;
        }
        Settings.LastDeviceId = device.Id;
        Settings.Save();
        Engine.Connect(device);
    }

    private void ToggleLast()
    {
        if (Engine.Device is { } current && IsActive(current))
        {
            _ = Engine.DisconnectAsync();
            return;
        }
        var device = Devices.FirstOrDefault(d => d.Id == Settings.LastDeviceId) ?? Devices.FirstOrDefault();
        if (device != null) ToggleDevice(device);
        else _flyout.ShowNearTray();
    }

    public void RequestFirewall()
    {
        if (RequestFirewallNow() && Engine.Device is { } device) Engine.Connect(device);
    }

    private bool RequestFirewallNow()
    {
        string? error = FirewallRule.Request();
        if (error == null) return true;
        MessageBox.Show($"İzin eklenemedi.\n\n{error}", "PodBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    public void SetLatency(LatencyMode mode)
    {
        if (Settings.Latency == mode) return;
        Settings.Latency = mode;
        Settings.Save();
        _flyout.Sync();
        // Gecikme oturum kurulurken belirlenir, bu yüzden etkin akış yeniden kurulur.
        if (Engine.State == BridgeState.Streaming && Engine.Device is { } device) Engine.Connect(device);
    }

    public void EditLatencyPresets()
    {
        _flyout.Hide();
        using var editor = new PresetEditorForm(Settings);
        if (editor.ShowDialog() != DialogResult.OK) return;

        int previous = Settings.BufferMs(Settings.Latency);
        Settings.LowBufferMs = editor.LowMs;
        Settings.BalancedBufferMs = editor.BalancedMs;
        Settings.StableBufferMs = editor.StableMs;
        Settings.Save();

        // Etkin ayarın süresi değiştiyse yeni tampon ancak yeni oturumla geçerli olur.
        if (Settings.BufferMs(Settings.Latency) != previous
            && Engine.State == BridgeState.Streaming && Engine.Device is { } device)
        {
            Engine.Connect(device);
        }
        _flyout.Sync();
    }

    public void ToggleMuteLocal()
    {
        bool value = !Settings.MuteLocal;
        Task.Run(() => Engine.SetMuteLocal(value));
        Settings.MuteLocal = value;
        _flyout.Sync();
    }

    public void ToggleAutoConnect()
    {
        Settings.AutoConnect = !Settings.AutoConnect;
        Settings.Save();
        _flyout.Sync();
    }

    public bool AutoStart
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
    }

    public void ToggleAutoStart()
    {
        SetAutoStart(!AutoStart);
        _flyout.Sync();
    }

    private static void SetAutoStart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, false);
    }

    private static string StartMenuShortcut => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PodBridge.lnk");

    private static void CreateStartMenuShortcut()
    {
        try
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            dynamic link = shell.CreateShortcut(StartMenuShortcut);
            link.TargetPath = Environment.ProcessPath;
            link.Description = "Windows sesini HomePod'a aktarır";
            link.Save();
        }
        catch (Exception ex)
        {
            Log.Write($"Başlat menüsü kısayolu: {ex.Message}");
        }
    }

    private void Uninstall()
    {
        var answer = MessageBox.Show(
            "PodBridge otomatik başlatması, Başlat menüsü kısayolu, ayarları ve güvenlik duvarı kuralı kaldırılacak. " +
            "Son olarak uygulamanın bulunduğu klasörü silebilirsin.\n\nDevam edilsin mi?",
            "PodBridge", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (answer != DialogResult.Yes) return;

        string? firewallError = FirewallRule.Exists() ? FirewallRule.RequestRemoval() : null;
        SetAutoStart(false);
        try
        {
            File.Delete(StartMenuShortcut);
            Directory.Delete(Settings.Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Kaldırma: {ex.Message}");
        }
        if (firewallError != null)
            MessageBox.Show($"Güvenlik duvarı kuralı silinemedi: {firewallError}", "PodBridge");
        Quit(saveNothing: true);
    }

    private void Quit(bool saveNothing = false)
    {
        _tray.Visible = false;
        _browseTimer.Stop();
        _hotkeys.Dispose();
        Engine.Dispose();
        ExitThread();
    }

    public void ShowPanel() => _flyout.ShowNearTray();

    /// <summary>Sınama: panelin görüntüsünü dosyaya yazar ve simgeyi kaldırır.</summary>
    public void RenderPanelAndExit(string path)
    {
        _flyout.RenderTo(path);
        _tray.Visible = false;
    }

    // ---------------------------------------------------------------- durum

    private void OnEngineChanged()
    {
        var state = Engine.State;
        _tray.Icon = state switch
        {
            BridgeState.Streaming => _activeIcon,
            BridgeState.NeedsFirewall or BridgeState.Failed or BridgeState.Reconnecting or BridgeState.TakenOver => _warnIcon,
            _ => _idleIcon,
        };

        string text = state == BridgeState.Streaming ? $"PodBridge: {Engine.Device?.Name}" : "PodBridge";
        _tray.Text = text.Length > 63 ? text[..63] : text;

        if (state != _lastState || Engine.Device != _lastDevice)
        {
            if (state == BridgeState.TakenOver)
                _tray.ShowBalloonTip(4000, "PodBridge", "HomePod başka bir cihaza geçti. Geri almak için panelden seç.", ToolTipIcon.Info);
            else if (state == BridgeState.Failed)
                _tray.ShowBalloonTip(5000, "PodBridge", Engine.Detail, ToolTipIcon.Warning);
            else if (state == BridgeState.NeedsFirewall)
                _flyout.ShowNearTray();
            _lastState = state;
            _lastDevice = Engine.Device;
        }
        _flyout.Sync();
    }

    private static Icon DrawIcon(Color accent)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var body = new SolidBrush(accent);
            using var shell = new GraphicsPath();
            shell.AddEllipse(5, 1, 22, 30);
            g.FillPath(body, shell);
            using var mesh = new Pen(Color.FromArgb(150, Color.White), 2);
            for (int y = 9; y <= 23; y += 5) g.DrawLine(mesh, 10, y, 22, y);
            using var top = new SolidBrush(Color.White);
            g.FillEllipse(top, 12, 3, 8, 4);
        }
        IntPtr handle = bitmap.GetHicon();
        return (Icon)Icon.FromHandle(handle).Clone();
    }

    /// <summary>Ctrl+Alt+P kısayolu için görünmez pencere.</summary>
    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const uint ModAlt = 1, ModControl = 2, ModNoRepeat = 0x4000;
        private readonly Action _action;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        public HotkeyWindow(Action action)
        {
            _action = action;
            CreateHandle(new CreateParams());
            if (!RegisterHotKey(Handle, HotkeyId, ModAlt | ModControl | ModNoRepeat, (uint)Keys.P))
                Log.Write("Ctrl+Alt+P başka bir uygulama tarafından kullanılıyor");
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey) _action();
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            UnregisterHotKey(Handle, HotkeyId);
            DestroyHandle();
        }
    }
}
