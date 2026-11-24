using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using PodBridge.Audio;

namespace PodBridge;

/// <summary>PodBridge --probe-keys: checks the volume key link without a HomePod. Presses the volume and mute keys
/// in software and verifies that PodBridge sees them the way it would while streaming. The speakers stay muted
/// during the test and their volume and mute state are restored afterwards.</summary>
internal static class KeyProbe
{
    private const int WmHotkey = 0x0312;
    private const byte VkVolumeMute = 0xAD, VkVolumeDown = 0xAE, VkVolumeUp = 0xAF;
    private const uint KeyUp = 0x2, ExtendedKey = 0x1;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private sealed class Window : NativeWindow
    {
        public int Hotkeys;
        public Window() => CreateHandle(new CreateParams());
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey) Hotkeys++;
            base.WndProc(ref m);
        }
    }

    private static void Press(byte key)
    {
        keybd_event(key, 0, ExtendedKey, IntPtr.Zero);
        keybd_event(key, 0, ExtendedKey | KeyUp, IntPtr.Zero);
    }

    private static void Pump(int milliseconds)
    {
        var until = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < until)
        {
            Application.DoEvents();
            Thread.Sleep(10);
        }
    }

    public static int Run()
    {
        int failures = 0;
        void Check(string name, bool ok)
        {
            Console.WriteLine($"{(ok ? "pass" : "FAIL")} {name}");
            if (!ok) failures++;
        }

        using var enumerator = new MMDeviceEnumerator();
        using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var endpoint = device.AudioEndpointVolume;
        float originalVolume = endpoint.MasterVolumeLevelScalar;
        bool originalMute = endpoint.Mute;
        Console.WriteLine($"Device: {device.FriendlyName}, volume {originalVolume * 100:F0}%, muted {originalMute}");

        var settings = Settings.Load();
        var seen = new List<double>();
        using var output = new LocalOutputControl(settings, percent => { lock (seen) seen.Add(percent); });
        try
        {
            // 1. Volume keys while streaming with "Mute computer speakers" on.
            output.Attach(device.ID, 30, muteSpeakers: true);
            Pump(300);
            Press(VkVolumeUp);
            Pump(400);
            Press(VkVolumeUp);
            Pump(400);
            Press(VkVolumeDown);
            Pump(600);
            double[] values;
            lock (seen) values = [.. seen];
            Console.WriteLine($"     HomePod volume requests: {string.Join(", ", values.Select(v => $"{v:F0}%"))}");
            Check("volume up reaches the HomePod", values.Length >= 2 && values[0] > 30 && values[1] > values[0]);
            Check("volume down reaches the HomePod", values.Length >= 3 && values[2] < values[1]);
            Check("speakers stay muted after volume keys", endpoint.Mute);

            // 2. The mute key goes to PodBridge instead of Windows while streaming.
            using var window = new Window();
            bool registered = RegisterHotKey(window.Handle, 1, 0x4000, VkVolumeMute);
            Check("mute key can be claimed", registered);
            if (registered)
            {
                bool before = endpoint.Mute;
                Press(VkVolumeMute);
                Pump(500);
                Check("mute key delivered to PodBridge", window.Hotkeys == 1);
                Check("mute key no longer toggles the speakers", endpoint.Mute == before);
                UnregisterHotKey(window.Handle, 1);
            }

            // 3. With "Mute computer speakers" off, the keys still drive the HomePod.
            output.Detach();
            lock (seen) seen.Clear();
            output.Attach(device.ID, 20, muteSpeakers: false);
            endpoint.Mute = true;   // Keep the test silent; the link reacts to the level, not the mute state.
            Pump(300);
            Press(VkVolumeUp);
            Pump(600);
            lock (seen) values = [.. seen];
            Console.WriteLine($"     HomePod volume requests: {string.Join(", ", values.Select(v => $"{v:F0}%"))}");
            Check("volume keys work with speakers not muted", values.Length >= 1 && values[^1] > 20);
        }
        finally
        {
            output.Detach();
            endpoint.MasterVolumeLevelScalar = originalVolume;
            endpoint.Mute = originalMute;
        }
        bool restored = Math.Abs(endpoint.MasterVolumeLevelScalar - originalVolume) < 0.01 && endpoint.Mute == originalMute;
        Check("speaker volume and mute restored", restored);
        Console.WriteLine(failures == 0 ? "ALL KEY CHECKS PASSED" : $"{failures} KEY CHECKS FAILED");
        return failures == 0 ? 0 : 1;
    }
}
