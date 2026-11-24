using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using PodBridge.Audio;
using PodBridge.Protocol;

namespace PodBridge.Ui;

/// <summary>Connects the tray icon, the panel and the engine. Runs on the Windows message loop.</summary>
public sealed class AppController : ApplicationContext
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "PodBridge";
    private const int HotkeyId = 0x5042;

    private readonly NotifyIcon _tray;
    private readonly FlyoutForm _flyout;
    private readonly HotkeyWindow _hotkeys;
    private readonly VolumeOsd _osd = new();
    private readonly StatusServer? _statusServer;
    private readonly System.Windows.Forms.Timer _browseTimer;
    private readonly SynchronizationContext _ui;
    private static readonly Color ActiveBadge = Color.FromArgb(76, 194, 255);
    private static readonly Color WarnBadge = Color.FromArgb(255, 185, 0);
    private Icon _idleIcon = Glyphs.TrayIcon(null);
    private Icon _activeIcon = Glyphs.TrayIcon(ActiveBadge);
    private Icon _warnIcon = Glyphs.TrayIcon(WarnBadge);
    private BridgeState _lastState;
    private AirPlayDevice? _lastDevice;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private static readonly TimeSpan UpdateCheckInterval = TimeSpan.FromHours(24);
    private bool _updateBusy;
    private Version? _updateReady;
    private bool _updateWaitAnnounced;

    public Settings Settings { get; }
    public BridgeEngine Engine { get; }
    public IReadOnlyList<AirPlayDevice> Devices { get; private set; } = [];
    public bool IsBrowsing { get; private set; }

    public AppController(Settings settings, bool afterUpdate = false)
    {
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        Settings = settings;
        LocalOutputControl.RestoreAfterCrash(settings);
        Engine = new BridgeEngine(settings);
        Engine.Changed += () => _ui.Post(_ => OnEngineChanged(), null);
        Devices = FilterDevices(settings.KnownDevices);

        _flyout = new FlyoutForm(this);
        _ = _flyout.Handle;

        _tray = new NotifyIcon
        {
            Icon = _idleIcon,
            Text = "PodBridge",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            if (_flyout.Visible) _flyout.Hide();
            else _flyout.ShowNearTray();
        };

        // The icon color has to follow the taskbar when it switches between light and dark.
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (e.Category == UserPreferenceCategory.General) _ui.Post(_ => RefreshTrayIcons(), null);
        };

        _hotkeys = new HotkeyWindow(ToggleLast,
            delta => { Engine.StepVolume(delta); ShowVolumeOsd(); },
            () => { Engine.ToggleHomePodMute(); ShowVolumeOsd(); });
        _browseTimer = new System.Windows.Forms.Timer { Interval = 30_000 };
        _browseTimer.Tick += (_, _) => _ = RefreshDevicesAsync();
        _browseTimer.Start();

        try
        {
            _statusServer = new StatusServer(() => new
            {
                app = "PodBridge",
                streaming = Engine.State == BridgeState.Streaming,
                latencyMs = Settings.EstimateEndToEndMs(Settings.BufferMs(Settings.Latency)),
                device = Engine.Device?.Name,
            });
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            // Audio streaming works without the browser extension.
            Log.Write($"Could not open the status endpoint for the browser extension: {ex.Message}");
        }

        FirstRun();
        _ = StartupAsync();

        if (afterUpdate)
            _tray.ShowBalloonTip(4000, "PodBridge", L.T($"Updated to version {Updater.Current}.", $"{Updater.Current} sürümüne güncellendi."), ToolTipIcon.Info);
        // First check a minute after start, so startup and auto-connect are not slowed down; then hourly ticks
        // that only go to GitHub once the last check is a day old.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 60_000 };
        _updateTimer.Tick += (_, _) =>
        {
            _updateTimer.Interval = 3_600_000;
            if (Settings.AutoUpdate && DateTime.UtcNow - Settings.LastUpdateCheck > UpdateCheckInterval)
                _ = CheckForUpdatesAsync(userAsked: false);
        };
        _updateTimer.Start();
    }

    // ---------------------------------------------------------------- updates

    private async Task CheckForUpdatesAsync(bool userAsked)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            Settings.LastUpdateCheck = DateTime.UtcNow;
            Settings.Save();
            var update = await Task.Run(() => Updater.CheckAsync(CancellationToken.None));
            if (update == null)
            {
                if (userAsked)
                    _tray.ShowBalloonTip(3000, "PodBridge", L.T($"You have the latest version ({Updater.Current}).", $"En son sürümü kullanıyorsun ({Updater.Current})."), ToolTipIcon.Info);
                return;
            }
            Log.Write($"Update {update.Version} available");
            if (!await Task.Run(() => Updater.DownloadAsync(update, CancellationToken.None)))
            {
                if (userAsked)
                    _tray.ShowBalloonTip(4000, "PodBridge", L.T("The update could not be verified and was not installed.", "Güncelleme doğrulanamadı, kurulmadı."), ToolTipIcon.Warning);
                return;
            }
            _updateReady = update.Version;
            RebuildMenu();
            InstallUpdateWhenIdle(userAsked);
        }
        catch (Exception ex)
        {
            // Offline, GitHub unreachable or rate limited: try again at the next check.
            Log.Write($"Update check failed: {ex.Message}");
            if (userAsked)
                _tray.ShowBalloonTip(3000, "PodBridge", L.T("Could not reach GitHub to check for updates.", "Güncellemeleri kontrol etmek için GitHub'a ulaşılamadı."), ToolTipIcon.Warning);
        }
        finally
        {
            _updateBusy = false;
        }
    }

    /// <summary>Installs right away unless audio is playing; while streaming it waits for the stream to stop
    /// (or the next start of PodBridge), so music is never cut off by an update.</summary>
    private void InstallUpdateWhenIdle(bool userAsked)
    {
        if (_updateReady is not { } version) return;
        bool busy = Engine.State is BridgeState.Streaming or BridgeState.Connecting or BridgeState.Reconnecting
                    || _flyout.Visible;
        if (busy && !userAsked)
        {
            if (!_updateWaitAnnounced)
                _tray.ShowBalloonTip(4000, "PodBridge", L.T($"Version {version} is ready. It installs when streaming stops.", $"{version} sürümü hazır. Akış durunca kurulacak."), ToolTipIcon.Info);
            _updateWaitAnnounced = true;
            return;
        }
        string error;
        try
        {
            if (Updater.TryApply(out error))
            {
                Quit();
                return;
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        Log.Write($"Could not install the update: {error}");
        _updateReady = null;
        RebuildMenu();
        _tray.ShowBalloonTip(5000, "PodBridge", L.T($"Version {version} could not be installed ({error}). Download it from GitHub.", $"{version} sürümü kurulamadı ({error}). GitHub'dan indirebilirsin."), ToolTipIcon.Warning);
    }

    /// <summary>After streaming stops, waits a little so that picking another HomePod or a quick reconnect is not
    /// interrupted, then installs if PodBridge is still idle and the panel is closed.</summary>
    private async void InstallAfterQuietPeriod()
    {
        await Task.Delay(TimeSpan.FromSeconds(30));
        if (_updateReady == null || _flyout.Visible || Engine.State is not (BridgeState.Idle or BridgeState.TakenOver)) return;
        InstallUpdateWhenIdle(userAsked: false);
    }

    private void ToggleAutoUpdate()
    {
        Settings.AutoUpdate = !Settings.AutoUpdate;
        Settings.Save();
        RebuildMenu();
    }

    private void RebuildMenu()
    {
        var old = _tray.ContextMenuStrip;
        _tray.ContextMenuStrip = BuildMenu();
        old?.Dispose();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add(L.T("Open panel", "Paneli aç"), null, (_, _) => _flyout.ShowNearTray());
        menu.Items.Add(L.T("Open log folder", "Günlük klasörünü aç"), null, (_, _) =>
            System.Diagnostics.Process.Start("explorer.exe", Settings.Folder));
        // Rarely changed, so it lives here instead of the panel. Each language is labeled in its own language.
        var language = new ToolStripMenuItem(L.T("Language", "Dil"));
        foreach (var (value, label) in new[] { (UiLanguage.English, "English"), (UiLanguage.Turkish, "Türkçe") })
            language.DropDownItems.Add(new ToolStripMenuItem(label, null, (_, _) => SetLanguage(value)) { Checked = L.Current == value });
        menu.Items.Add(language);
        menu.Items.Add(new ToolStripSeparator());
        if (_updateReady is { } ready)
            menu.Items.Add(L.T($"Install version {ready} and restart", $"{ready} sürümünü kur ve yeniden başlat"), null, (_, _) => InstallUpdateWhenIdle(userAsked: true));
        else
            menu.Items.Add(L.T("Check for updates", "Güncellemeleri denetle"), null, (_, _) => _ = CheckForUpdatesAsync(userAsked: true));
        menu.Items.Add(new ToolStripMenuItem(L.T("Update automatically", "Otomatik güncelle"), null, (_, _) => ToggleAutoUpdate())
        {
            Checked = Settings.AutoUpdate,
        });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(L.T("Uninstall…", "Kaldır…"), null, (_, _) => Uninstall());
        menu.Items.Add(L.T("Exit", "Çıkış"), null, (_, _) => Quit());
        return menu;
    }

    public void SetLanguage(UiLanguage language)
    {
        if (L.Current == language) return;
        L.Current = language;
        Settings.Language = language;
        Settings.Save();
        RebuildMenu();
        _flyout.Sync();
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
        _tray.ShowBalloonTip(5000, L.T("PodBridge is ready", "PodBridge hazır"),
            L.T("Click the tray icon and pick your HomePod. Ctrl+Alt+P toggles it quickly.", "Görev çubuğundaki simgeye tıkla ve HomePod'unu seç. Ctrl+Alt+P ile hızlıca aç/kapat."), ToolTipIcon.Info);
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
                // Devices seen before that are not answering right now stay in the list.
                var merged = Settings.KnownDevices.Where(k => homePods.All(d => d.Id != k.Id)).Concat(homePods);
                Settings.KnownDevices = FilterDevices(merged);
                Settings.Save();
                Devices = Settings.KnownDevices;
            }
        }
        catch (Exception ex)
        {
            Log.Write($"Device discovery: {ex.Message}");
        }
        IsBrowsing = false;
        _flyout.Sync();
    }

    // ---------------------------------------------------------------- actions

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
            Log.Write("No firewall rule, asking for permission");
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
        MessageBox.Show(L.T($"Could not add the permission.\n\n{error}", $"İzin eklenemedi.\n\n{error}"), "PodBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        return false;
    }

    public void SetLatency(LatencyMode mode)
    {
        if (Settings.Latency == mode) return;
        Settings.Latency = mode;
        Settings.Save();
        _flyout.Sync();
        // Latency is fixed when the session is set up, so an active stream is restarted.
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

        // If the active preset's buffer changed, it only takes effect with a new session.
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

    /// <summary>The keys never reach Windows while streaming, so its own volume pop-up does not appear; show ours.</summary>
    private void ShowVolumeOsd()
    {
        if (Engine.State == BridgeState.Streaming) _osd.ShowVolume(Engine.DisplayVolume, Engine.HomePodMuted);
    }

    public void ToggleVolumeKeys()
    {
        bool value = !Settings.VolumeKeys;
        Settings.VolumeKeys = value;
        Task.Run(() => Engine.SetVolumeKeys(value));
        UpdateVolumeKeys();
        _flyout.Sync();
    }

    /// <summary>While streaming (and unless turned off) the volume and mute keys go to PodBridge instead of Windows.
    /// Taking the keys means Windows never unmutes the computer's speakers, so nothing leaks from them.</summary>
    private void UpdateVolumeKeys() =>
        _hotkeys.SetVolumeKeysActive(Engine.State == BridgeState.Streaming && Settings.VolumeKeys);

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
            link.Description = L.T("Streams Windows audio to HomePod", "Windows sesini HomePod'a aktarır");
            link.Save();
        }
        catch (Exception ex)
        {
            Log.Write($"Start menu shortcut: {ex.Message}");
        }
    }

    private void Uninstall()
    {
        var answer = MessageBox.Show(
            L.T("This removes PodBridge's autostart entry, Start menu shortcut, settings and firewall rule. " +
                "Afterwards you can delete the folder the app is in.\n\nContinue?",
                "PodBridge'in otomatik başlatması, Başlat menüsü kısayolu, ayarları ve güvenlik duvarı kuralı kaldırılacak. " +
                "Ardından uygulamanın bulunduğu klasörü silebilirsin.\n\nDevam edilsin mi?"),
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
            Log.Write($"Uninstall: {ex.Message}");
        }
        if (firewallError != null)
            MessageBox.Show(L.T($"Could not remove the firewall rule: {firewallError}", $"Güvenlik duvarı kuralı silinemedi: {firewallError}"), "PodBridge");
        Quit(saveNothing: true);
    }

    private void Quit(bool saveNothing = false)
    {
        _tray.Visible = false;
        _browseTimer.Stop();
        _updateTimer.Stop();
        _hotkeys.Dispose();
        _osd.Dispose();
        _statusServer?.Dispose();
        Engine.Dispose();
        ExitThread();
    }

    public void ShowPanel() => _flyout.ShowNearTray();

    /// <summary>Testing: writes an image of the panel to a file and removes the tray icon.</summary>
    public void RenderPanelAndExit(string path)
    {
        _flyout.RenderTo(path);
        _tray.Visible = false;
    }

    // ---------------------------------------------------------------- state

    private Icon CurrentIcon => Engine.State switch
    {
        BridgeState.Streaming => _activeIcon,
        BridgeState.NeedsFirewall or BridgeState.Failed or BridgeState.Reconnecting or BridgeState.TakenOver => _warnIcon,
        _ => _idleIcon,
    };

    private void RefreshTrayIcons()
    {
        Icon[] old = [_idleIcon, _activeIcon, _warnIcon];
        _idleIcon = Glyphs.TrayIcon(null);
        _activeIcon = Glyphs.TrayIcon(ActiveBadge);
        _warnIcon = Glyphs.TrayIcon(WarnBadge);
        _tray.Icon = CurrentIcon;
        foreach (var icon in old) icon.Dispose();
        _flyout.Sync();
    }

    private void OnEngineChanged()
    {
        var state = Engine.State;
        _tray.Icon = CurrentIcon;

        string text = state == BridgeState.Streaming ? $"PodBridge: {Engine.Device?.Name}" : "PodBridge";
        _tray.Text = text.Length > 63 ? text[..63] : text;

        if (state != _lastState || Engine.Device != _lastDevice)
        {
            if (state == BridgeState.TakenOver)
                _tray.ShowBalloonTip(4000, "PodBridge", L.T("The HomePod switched to another device. Pick it in the panel to take it back.", "HomePod başka bir cihaza geçti. Geri almak için panelden seç."), ToolTipIcon.Info);
            else if (state == BridgeState.Failed)
                _tray.ShowBalloonTip(5000, "PodBridge", Engine.Detail, ToolTipIcon.Warning);
            else if (state == BridgeState.NeedsFirewall)
                _flyout.ShowNearTray();
            UpdateVolumeKeys();
            if (_updateReady != null && Settings.AutoUpdate && state is BridgeState.Idle or BridgeState.TakenOver)
                InstallAfterQuietPeriod();
            _lastState = state;
            _lastDevice = Engine.Device;
        }
        _flyout.Sync();
    }

    /// <summary>Invisible window for the Ctrl+Alt+P hotkey and, while streaming, the volume and mute keys.</summary>
    private sealed class HotkeyWindow : NativeWindow, IDisposable
    {
        private const int WmHotkey = 0x0312;
        private const uint ModAlt = 1, ModControl = 2, ModNoRepeat = 0x4000;
        private const int MuteHotkeyId = HotkeyId + 1, DownHotkeyId = HotkeyId + 2, UpHotkeyId = HotkeyId + 3;
        private const uint VkVolumeMute = 0xAD, VkVolumeDown = 0xAE, VkVolumeUp = 0xAF;
        private const int VolumeStep = 2;
        private readonly Action _action;
        private readonly Action<int> _step;
        private readonly Action _mute;
        private bool _volumeKeysRegistered;

        [DllImport("user32.dll")]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        public HotkeyWindow(Action action, Action<int> step, Action mute)
        {
            _action = action;
            _step = step;
            _mute = mute;
            CreateHandle(new CreateParams());
            if (!RegisterHotKey(Handle, HotkeyId, ModAlt | ModControl | ModNoRepeat, (uint)Keys.P))
                Log.Write("Ctrl+Alt+P is already used by another app");
        }

        /// <summary>A registered hotkey goes to this window instead of Windows. Volume up and down repeat while held,
        /// like the Windows keys do; mute does not.</summary>
        public void SetVolumeKeysActive(bool active)
        {
            if (active == _volumeKeysRegistered) return;
            if (active)
            {
                bool up = RegisterHotKey(Handle, UpHotkeyId, 0, VkVolumeUp);
                bool down = RegisterHotKey(Handle, DownHotkeyId, 0, VkVolumeDown);
                bool mute = RegisterHotKey(Handle, MuteHotkeyId, ModNoRepeat, VkVolumeMute);
                if (!(up && down && mute)) Log.Write($"Some volume keys are taken by another app (up {up}, down {down}, mute {mute})");
            }
            else
            {
                UnregisterHotKey(Handle, UpHotkeyId);
                UnregisterHotKey(Handle, DownHotkeyId);
                UnregisterHotKey(Handle, MuteHotkeyId);
            }
            _volumeKeysRegistered = active;
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey)
            {
                int id = (int)m.WParam;
                if (id == UpHotkeyId) _step(VolumeStep);
                else if (id == DownHotkeyId) _step(-VolumeStep);
                else if (id == MuteHotkeyId) _mute();
                else _action();
            }
            base.WndProc(ref m);
        }

        public void Dispose()
        {
            SetVolumeKeysActive(false);
            UnregisterHotKey(Handle, HotkeyId);
            DestroyHandle();
        }
    }
}
