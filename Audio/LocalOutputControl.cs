using NAudio.CoreAudioApi;

namespace PodBridge.Audio;

/// <summary>Akış sürerken bilgisayar hoparlörünü susturur ve Windows ses seviyesini HomePod'a bağlar:
/// ses tuşları ve görev çubuğu kaydırıcısı doğrudan HomePod'u kontrol eder. Bırakınca eski hâline döner.
/// Loopback, uç nokta sessizliğinden etkilenmez.</summary>
public sealed class LocalOutputControl : IDisposable
{
    private const float Epsilon = 0.004f;

    private readonly Settings _settings;
    private readonly Action<double> _onVolume;
    private MMDeviceEnumerator? _enumerator;
    private MMDevice? _device;
    private float _lastScalar;
    private int _remuting;

    public LocalOutputControl(Settings settings, Action<double> onVolume)
    {
        _settings = settings;
        _onVolume = onVolume;
    }

    /// <summary>Önceki çalışmada çökme olduysa hoparlörü eski hâline getirir.</summary>
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
            Log.Write($"Hoparlör durumu geri yüklenemedi: {ex.Message}");
        }
        settings.PendingRestore = null;
        settings.Save();
    }

    public void Attach(string deviceId, double homePodPercent)
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

        _lastScalar = (float)(homePodPercent / 100.0);
        volume.MasterVolumeLevelScalar = _lastScalar;
        volume.Mute = true;
        volume.OnVolumeNotification += OnNotification;
    }

    /// <summary>Kaydırıcı arayüzden değişince Windows göstergesini de hizalar.</summary>
    public void Reflect(double homePodPercent)
    {
        if (_device == null) return;
        _lastScalar = (float)(homePodPercent / 100.0);
        try { _device.AudioEndpointVolume.MasterVolumeLevelScalar = _lastScalar; }
        catch (Exception ex) { Log.Write($"Windows ses seviyesi ayarlanamadı: {ex.Message}"); }
    }

    private void OnNotification(AudioVolumeNotificationData data)
    {
        // Windows, ses açma tuşuna basılınca sessizliği kaldırır; hoparlör bağırmasın diye hemen geri alıyoruz.
        if (!data.Muted && Interlocked.Exchange(ref _remuting, 1) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { if (_device != null) _device.AudioEndpointVolume.Mute = true; }
                catch (Exception ex) { Log.Write($"Yeniden susturma: {ex.Message}"); }
                Volatile.Write(ref _remuting, 0);
            });
        }

        if (Math.Abs(data.MasterVolume - _lastScalar) > Epsilon)
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
                Log.Write($"Hoparlör geri yüklenemedi: {ex.Message}");
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
