using System.Net;
using PodBridge.Audio;
using PodBridge.Protocol;
using PodBridge.Ui;

namespace PodBridge;

internal static class Program
{
    private const string MutexName = @"Local\PodBridge.Instance";
    private const string ShowEventName = @"Local\PodBridge.Show";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == FirewallRule.ApplyArgument)
            return FirewallRule.ApplyElevated(true, args.ElementAtOrDefault(1));
        if (args.Length >= 1 && args[0] == FirewallRule.RemoveArgument)
            return FirewallRule.ApplyElevated(false, args.ElementAtOrDefault(1));
        if (args.Contains("--selftest"))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return SelfTest.Run();
        }
        if (args.Contains("--test"))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return Task.Run(() => RunTest(args)).GetAwaiter().GetResult();
        }
        if (args.Contains("--measure"))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return Task.Run(() => LatencyMeter.RunAsync(args)).GetAwaiter().GetResult();
        }
        if (Option(args, "--render-ui") is { } imagePath)
        {
            ApplicationConfiguration.Initialize();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            var settings = new Settings { FirstRunDone = true };
            settings.KnownDevices.Add(new AirPlayDevice("AA:BB:CC:DD:EE:01", "Living Room", "AudioAccessory5,1", "192.168.1.50", 7000));
            L.Current = Option(args, "--language") == "tr" ? UiLanguage.Turkish : UiLanguage.English;
            new AppController(settings).RenderPanelAndExit(imagePath);
            return 0;
        }
        if (Option(args, "--render-presets") is { } editorImage)
        {
            ApplicationConfiguration.Initialize();
            L.Current = Option(args, "--language") == "tr" ? UiLanguage.Turkish : UiLanguage.English;
            using var editor = new PresetEditorForm(new Settings { LowBufferMs = 80 });
            // Off-screen rendering does not draw the title bar and text boxes faithfully, so show the window briefly and grab the screen.
            editor.Show();
            for (int i = 0; i < 20; i++)
            {
                Application.DoEvents();
                Thread.Sleep(25);
            }
            using var bitmap = new Bitmap(editor.Width, editor.Height);
            using (var g = Graphics.FromImage(bitmap)) g.CopyFromScreen(editor.Location, Point.Empty, editor.Size);
            bitmap.Save(editorImage);
            editor.Close();
            return 0;
        }
        if (Option(args, "--write-extension-icons") is { } iconFolder)
        {
            Directory.CreateDirectory(iconFolder);
            foreach (int size in new[] { 16, 32, 48, 128 })
            {
                using var icon = Ui.Glyphs.AppIcon(size);
                icon.Save(Path.Combine(iconFolder, $"{size}.png"));
            }
            return 0;
        }
        if (Option(args, "--write-icon") is { } iconPath)
        {
            Ui.Glyphs.WriteIco(iconPath, 16, 20, 24, 32, 40, 48, 64, 256);
            using var preview = Ui.Glyphs.AppIcon(256);
            preview.Save(Path.ChangeExtension(iconPath, ".png"));
            using var small = Ui.Glyphs.AppIcon(32);
            using var smallLarge = new Bitmap(128, 128);
            using (var g = Graphics.FromImage(smallLarge))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                g.DrawImage(small, 0, 0, 128, 128);
            }
            smallLarge.Save(Path.Combine(Path.GetDirectoryName(iconPath)!, "app-32px.png"));
            foreach (var (name, badge) in new (string, Color?)[] { ("idle", null), ("active", Color.FromArgb(76, 194, 255)), ("warn", Color.FromArgb(255, 185, 0)) })
            {
                using var icon = Ui.Glyphs.TrayIcon(badge);
                using var bitmap = icon.ToBitmap();
                using var large = new Bitmap(bitmap, bitmap.Width * 4, bitmap.Height * 4);
                large.Save(Path.Combine(Path.GetDirectoryName(iconPath)!, $"tray-{name}.png"));
            }
            return 0;
        }
        if (args.Contains("--sweep"))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return Task.Run(() => LatencyMeter.SweepAsync(args)).GetAwaiter().GetResult();
        }
        if (args.Contains("--audio-status"))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return Task.Run(LatencyMeter.PrintAudioStatus).GetAwaiter().GetResult();
        }
        if (args.Contains("--probe-volume"))
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            return Task.Run(LatencyMeter.ProbeVolumeAsync).GetAwaiter().GetResult();
        }
        return RunApplication();
    }

    private static int RunApplication()
    {
        using var mutex = new Mutex(true, MutexName, out bool first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        bool afterUpdate = args.Contains(Updater.AfterUpdateArgument);
        if (!first && afterUpdate)
        {
            // Started by the previous version, which is still shutting down; wait for it to let go.
            try { first = mutex.WaitOne(TimeSpan.FromSeconds(20)); }
            catch (AbandonedMutexException) { first = true; }
        }
        if (!first)
        {
            showEvent.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"UI error: {e.Exception}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"Task error: {e.Exception.InnerException?.Message}");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Unhandled error: {e.ExceptionObject}");

        // The Windows Forms synchronization context must exist before the controller is created.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var settings = Settings.Load();
        L.Current = settings.Language;

        // An update downloaded in an earlier run is installed now, before anything streams.
        if (!afterUpdate && settings.AutoUpdate && Updater.Staged() != null)
        {
            try
            {
                if (Updater.TryApply(out string error)) return 0;
                Log.Write($"Could not install the update: {error}");
            }
            catch (Exception ex)
            {
                Log.Write($"Could not install the update: {ex.Message}");
            }
        }
        if (afterUpdate) Updater.CleanUpOldVersion();

        var controller = new AppController(settings, afterUpdate);

        // If the app is launched a second time, the new process exits and the running one shows its panel.
        var ui = SynchronizationContext.Current!;
        ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => ui.Post(_ => controller.ShowPanel(), null),
                                               null, Timeout.Infinite, false);
        Application.Run(controller);
        return 0;
    }

    internal static string? Option(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // Command-line test: PodBridge --test --ip 192.168.1.50 --seconds 40 [--tone 440] [--loopback] [--volume 25] [--latency 22050]
    private static async Task<int> RunTest(string[] args)
    {
        var ip = IPAddress.Parse(Option(args, "--ip") ?? throw new ArgumentException("--ip is required"));
        int seconds = int.Parse(Option(args, "--seconds") ?? "10");
        int latency = int.Parse(Option(args, "--latency") ?? "66150");
        double toneHz = double.Parse(Option(args, "--tone") ?? "0");
        double? volume = Option(args, "--volume") is { } v ? double.Parse(v) : null;
        void Print(string line) => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");

        var closed = new TaskCompletionSource<SessionCloseReason>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var session = new AirPlaySession(ip, 7000, new SessionOptions("PodBridge", "AA:BB:CC:11:22:33", latency), Print);
        session.Closed += (reason, _) => closed.TrySetResult(reason);

        try
        {
            await session.ConnectAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            Print($"CONNECTION FAILED [{ex.GetType().Name}]: {ex.Message}");
            return 2;
        }

        using var loopback = args.Contains("--loopback") ? new LoopbackSource(Print) : null;
        var tone = new ToneSource(toneHz);
        session.StartStreaming(loopback != null ? loopback : tone);
        Print($"Streaming. Current volume: {await session.GetVolumePercentAsync():F0}%");
        if (volume is { } target) await session.SetVolumeAsync(target);
        await Task.Delay(300);
        tone.Enabled = true;

        var finished = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        if (finished == closed.Task)
        {
            Print($"SESSION CLOSED EARLY: {closed.Task.Result} ({session.PacketsSent} packets)");
            return 3;
        }
        Print($"Done: {seconds} s, {session.PacketsSent} packets, {session.PacketsRetransmitted} retransmits, " +
              $"{loopback?.Underruns ?? 0} underruns");
        await session.StopAsync();
        return 0;
    }
}

internal sealed class ToneSource(double hz) : IPcmSource
{
    private double _phase;
    public volatile bool Enabled;

    public void Read(Span<short> interleaved)
    {
        double step = 2 * Math.PI * hz / AirPlaySession.SampleRate;
        for (int i = 0; i < interleaved.Length; i += 2)
        {
            short sample = Enabled && hz > 0 ? (short)(Math.Sin(_phase) * 6000) : (short)0;
            interleaved[i] = interleaved[i + 1] = sample;
            _phase += step;
        }
    }
}
