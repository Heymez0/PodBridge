using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using PodBridge.Audio;

namespace PodBridge;

/// <summary>PodBridge --probe-keys: checks the volume key handling without a HomePod. Presses the volume and mute keys
/// in software while they are claimed the way PodBridge claims them during streaming, and checks that they arrive and
/// that the computer's speakers are neither unmuted nor changed. Volume and mute are restored afterwards.</summary>
internal static class KeyProbe
{
    private const int WmHotkey = 0x0312;
    private const byte VkVolumeMute = 0xAD, VkVolumeDown = 0xAE, VkVolumeUp = 0xAF;
    private const uint KeyUp = 0x2, ExtendedKey = 0x1, ModNoRepeat = 0x4000;
    private const int UpId = 1, DownId = 2, MuteId = 3;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, IntPtr extra);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    private sealed class Window : NativeWindow, IDisposable
    {
        public readonly List<int> Received = [];
        public Window() => CreateHandle(new CreateParams());
        public void Dispose() => DestroyHandle();
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmHotkey) Received.Add((int)m.WParam);
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
            // 1. Keys claimed as during streaming: they arrive here and never reach the speakers.
            output.Attach(device.ID, 30, muteSpeakers: true, linkVolume: true);
            Pump(300);
            float levelBefore = endpoint.MasterVolumeLevelScalar;
            bool unmutedSeen = false;
            void Watch(AudioVolumeNotificationData data) { if (!data.Muted) unmutedSeen = true; }
            endpoint.OnVolumeNotification += Watch;

            using var window = new Window();
            bool claimed = RegisterHotKey(window.Handle, UpId, 0, VkVolumeUp)
                           & RegisterHotKey(window.Handle, DownId, 0, VkVolumeDown)
                           & RegisterHotKey(window.Handle, MuteId, ModNoRepeat, VkVolumeMute);
            Check("volume and mute keys can be claimed", claimed);
            Press(VkVolumeUp);
            Pump(300);
            Press(VkVolumeUp);
            Pump(300);
            Press(VkVolumeDown);
            Pump(300);
            Press(VkVolumeMute);
            Pump(500);
            UnregisterHotKey(window.Handle, UpId);
            UnregisterHotKey(window.Handle, DownId);
            UnregisterHotKey(window.Handle, MuteId);
            endpoint.OnVolumeNotification -= Watch;

            Console.WriteLine($"     keys received: {string.Join(", ", window.Received.Select(id => id switch { UpId => "up", DownId => "down", _ => "mute" }))}");
            Check("all four key presses reach PodBridge", window.Received.SequenceEqual([UpId, UpId, DownId, MuteId]));
            Check("speakers never unmuted (nothing plays from the computer)", !unmutedSeen && endpoint.Mute);
            Check("Windows volume not changed by the keys", Math.Abs(endpoint.MasterVolumeLevelScalar - levelBefore) < 0.001);

            // 2. The taskbar slider still drives the HomePod while linked.
            lock (seen) seen.Clear();
            endpoint.MasterVolumeLevelScalar = 0.44f;
            Pump(500);
            double[] values;
            lock (seen) values = [.. seen];
            Check("taskbar slider reaches the HomePod", values.Length >= 1 && Math.Abs(values[^1] - 44) < 1);

            // 3. With the option off, Windows volume changes stay on the computer.
            output.Detach();
            output.Attach(device.ID, 30, muteSpeakers: true, linkVolume: false);
            lock (seen) seen.Clear();
            endpoint.MasterVolumeLevelScalar = 0.24f;
            Pump(500);
            lock (seen) values = [.. seen];
            Check("option off: the HomePod is left alone", values.Length == 0);
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
