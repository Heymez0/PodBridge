using NAudio.CoreAudioApi;

namespace PodBridge.Audio;

/// <summary>While streaming, ties the Windows volume to the HomePod: the volume keys and the taskbar slider control
/// the HomePod directly. Optionally also mutes the computer's speakers. Everything is restored on release.
/// Loopback capture is not affected by the endpoint volume or mute.</summary>
public sealed class LocalOutputControl : IDisposable
{
    private const float Epsilon = 0.004f;

    private readonly Settings _settings;
    private readonly Action<double> _onVolume;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private float _lastScalar;
    private int _remuting;
    private bool _muteSpeakers;
    private bool _linkVolume;

    public LocalOutputControl(Settings settings, Action<double> onVolume)
    {
        _settings = settings;
        _onVolume = onVolume;
    }

    /// <summary>Restores the speakers if the previous run crashed.</summary>
    public static void RestoreAfterCrash(Settings settings)
    {
        if (settings.PendingRestore is not { } saved) return;
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            using var device = enumerator.GetDevice(saved.DeviceId);
            device.AudioEndpointVolume.MasterVolumeLevelScalar = saved.Volume;
            device.AudioEndpointVolume.Mute = saved.Muted;
        }
        catch (Exception ex)
        {
            Log.Write($"Could not restore speaker state: {ex.Message}");
        }
        settings.PendingRestore = null;
        settings.Save();
    }

    public void Attach(string deviceId, double homePodPercent, bool muteSpeakers, bool linkVolume)
    {
        Detach();
        _enumerator = new MMDeviceEnumerator();
        _device = _enumerator.GetDevice(deviceId);
        var volume = _device.AudioEndpointVolume;

        _settings.PendingRestore = new LocalAudioSnapshot
        {
            DeviceId = deviceId,
            Volume = volume.MasterVolumeLevelScalar,
            Muted = volume.Mute,
        };
        _settings.Save();

        _muteSpeakers = muteSpeakers;
        _linkVolume = linkVolume;
        _lastScalar = volume.MasterVolumeLevelScalar;
        if (linkVolume)
        {
            // The Windows slider shows the HomePod's level while they are linked.
            _lastScalar = (float)(homePodPercent / 100.0);
            volume.MasterVolumeLevelScalar = _lastScalar;
        }
        // Without muting, the speakers simply play at the HomePod's level.
        volume.Mute = muteSpeakers;
        volume.OnVolumeNotification += OnNotification;
    }

    /// <summary>Keeps the Windows indicator in line when the slider in our UI moves.</summary>
    public void Reflect(double homePodPercent)
    {
        if (_device == null || !_linkVolume) return;
        _lastScalar = (float)(homePodPercent / 100.0);
        try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = _lastScalar; }
        catch (Exception ex) { Log.Write($"Could not set the Windows volume: {ex.Message}"); }
    }

    private void OnNotification(AudioVolumeNotificationData data)
    {
        // Windows unmutes when the volume-up key is pressed; mute again right away so the speakers do not blare.
        if (_muteSpeakers && !data.Muted && Interlocked.Exchange(ref _remuting, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (_device != null) _device.AudioEndpointVolume.Mute = true; }
                catch (Exception ex) { Log.Write($"Re-mute: {ex.Message}"); }
                Volatile.Write(ref _remuting, 0);
            });
        }

        // The taskbar slider and other apps change the level here; the volume keys are taken by PodBridge directly.
        if (_linkVolume && Math.Abs(data.MasterVolume - _lastScalar) > Epsilon)
        {
            _lastScalar = data.MasterVolume;
            _onVolume(data.MasterVolume * 100.0);
        }
    }

    public void Detach()
    {
        var device = _device;
        _device = null;
        if (device != null)
        {
            try
            {
                device.AudioEndpointVolume.OnVolumeNotification -= OnNotification;
                if (_settings.PendingRestore is { } saved)
                {
                    device.AudioEndpointVolume.MasterVolumeLevelScalar = saved.Volume;
                    device.AudioEndpointVolume.Mute = saved.Muted;
                }
            }
            catch (Exception ex)
            {
                Log.Write($"Could not restore the speakers: {ex.Message}");
            }
            device.Dispose();
        }
        _enumerator?.Dispose();
        _enumerator = null;
        if (_settings.PendingRestore != null)
        {
            _settings.PendingRestore = null;
            _settings.Save();
        }
    }

    public void Dispose() => Detach();
}
