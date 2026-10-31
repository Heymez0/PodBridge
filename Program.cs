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
            settings.KnownDevices.Add(new AirPlayDevice("AA:BB:CC:DD:EE:01", "Çalışma Odası", "AudioAccessory5,1", "192.168.1.50", 7000));
            new AppController(settings).RenderPanelAndExit(imagePath);
            return 0;
        }
        if (Option(args, "--render-presets") is { } editorImage)
        {
            ApplicationConfiguration.Initialize();
            using var editor = new PresetEditorForm(new Settings { LowBufferMs = 80 });
            editor.StartPosition = FormStartPosition.Manual;
            editor.Location = new Point(-4000, -4000);
            editor.ShowInTaskbar = false;
            editor.Show();
            Application.DoEvents();
            using var bitmap = new Bitmap(editor.Width, editor.Height);
            editor.DrawToBitmap(bitmap, new Rectangle(0, 0, editor.Width, editor.Height));
            bitmap.Save(editorImage);
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
        if (!first)
        {
            showEvent.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Write($"Arayüz hatası: {e.Exception}");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Write($"Görev hatası: {e.Exception.InnerException?.Message}");
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"Beklenmeyen hata: {e.ExceptionObject}");

        // Windows Forms eşitleme bağlamı, denetleyici oluşturulmadan önce hazır olmalı.
        SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        var controller = new AppController(Settings.Load());

        // Uygulama ikinci kez açılırsa yeni süreç kapanır, var olan panel açılır.
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

    // Komut satırı testi: PodBridge --test --ip 192.168.1.50 --seconds 40 [--tone 440] [--loopback] [--volume 25] [--latency 22050]
    private static async Task<int> RunTest(string[] args)
    {
        var ip = IPAddress.Parse(Option(args, "--ip") ?? throw new ArgumentException("--ip gerekli"));
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
            Print($"BAĞLANTI HATASI [{ex.GetType().Name}]: {ex.Message}");
            return 2;
        }

        using var loopback = args.Contains("--loopback") ? new LoopbackSource(Print) : null;
        var tone = new ToneSource(toneHz);
        session.StartStreaming(loopback != null ? loopback : tone);
        Print($"Akış başladı. Mevcut ses: {await session.GetVolumePercentAsync():F0}%");
        if (volume is { } target) await session.SetVolumeAsync(target);
        await Task.Delay(300);
        tone.Enabled = true;

        var finished = await Task.WhenAny(closed.Task, Task.Delay(TimeSpan.FromSeconds(seconds)));
        if (finished == closed.Task)
        {
            Print($"OTURUM ERKEN KAPANDI: {closed.Task.Result} ({session.PacketsSent} paket)");
            return 3;
        }
        Print($"Tamam: {seconds} sn, {session.PacketsSent} paket, {session.PacketsRetransmitted} yeniden gönderim, " +
              $"{loopback?.Underruns ?? 0} yetersizlik");
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
