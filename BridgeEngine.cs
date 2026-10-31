using System.Net;
using Microsoft.Win32;
using PodBridge.Audio;
using PodBridge.Protocol;

namespace PodBridge;

public enum BridgeState
{
    Idle,
    Connecting,
    Streaming,
    Reconnecting,
    /// <summary>Başka bir cihaz HomePod'u devraldı; kullanıcı istemeden geri alınmaz.</summary>
    TakenOver,
    NeedsFirewall,
    Failed,
}

/// <summary>Yakalama, oturum ve yeniden bağlanmayı yönetir. Olaylar arka plan iş parçacığında gelir.</summary>
public sealed class BridgeEngine : IDisposable
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 15, 30];
    private static readonly TimeSpan BrowseWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumStableUptime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(30);

    private readonly Settings _settings;
    private readonly LocalOutputControl _localOutput;
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private Task _runTask = Task.CompletedTask;
    private AirPlaySession? _session;
    private LoopbackSource? _source;
    private string? _capturedDeviceId;
    private bool _localAttached;
    private double _volume = -1;
    private DateTime _lastVolumeSent;
    private readonly SemaphoreSlim _volumeGate = new(1, 1);

    public BridgeState State { get; private set; }
    public AirPlayDevice? Device { get; private set; }
    public string Detail { get; private set; } = "";
    public double Volume => _volume;
    public long? ReceiverLatencyMs => _session?.ReceiverLatencyMs;
    public long PacketsRetransmitted => _session?.PacketsRetransmitted ?? 0;
    public long Underruns => _source?.Underruns ?? 0;

    public event Action? Changed;

    public BridgeEngine(Settings settings)
    {
        _settings = settings;
        _localOutput = new LocalOutputControl(settings, percent => _ = ApplyVolumeAsync(percent, fromWindows: true));
        SystemEvents.PowerModeChanged += OnPowerChanged;
    }

    private void SetState(BridgeState state, string detail = "")
    {
        State = state;
        Detail = detail;
        Log.Write($"Durum: {state} {detail}");
        Changed?.Invoke();
    }

    public void Connect(AirPlayDevice device)
    {
        Task previous;
        CancellationTokenSource run;
        lock (_gate)
        {
            _run?.Cancel();
            previous = _runTask;
            run = _run = new CancellationTokenSource();
            Device = device;
            _runTask = Task.Run(() => RunAsync(device, previous, run.Token));
        }
    }

    public Task DisconnectAsync()
    {
        lock (_gate)
        {
            _run?.Cancel();
            return _runTask;
        }
    }

    private async Task RunAsync(AirPlayDevice device, Task previous, CancellationToken ct)
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception ex) { Log.Write($"Önceki oturum: {ex.Message}"); }
        if (ct.IsCancellationRequested) return;

        var source = new LoopbackSource(Log.Write);
        source.DeviceActive += OnCaptureDevice;
        _source = source;
        int attempt = 0;
        SetState(BridgeState.Connecting);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                DateTime startedAt = default;
                try
                {
                    var target = await ResolveAsync(device, attempt > 0, ct).ConfigureAwait(false);
                    device = target;
                    Device = target;

                    var options = new SessionOptions(Environment.MachineName, _settings.ClientId,
                                                     _settings.LatencyFrames());
                    using var session = new AirPlaySession(IPAddress.Parse(target.Address), target.Port, options, Log.Write);
                    var closed = new TaskCompletionSource<(SessionCloseReason Reason, string Detail)>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    session.Closed += (reason, detail) => closed.TrySetResult((reason, detail));

                    await session.ConnectAsync(ct).ConfigureAwait(false);
                    _session = session;
                    session.StartStreaming(source);
                    startedAt = DateTime.UtcNow;

                    if (await session.GetVolumePercentAsync().ConfigureAwait(false) is { } current) _volume = current;
                    else if (_volume < 0) _volume = 30;
                    AttachLocalOutput();

                    attempt = 0;
                    SetState(BridgeState.Streaming);

                    SessionCloseReason reason;
                    string closeDetail;
                    try
                    {
                        while (true)
                        {
                            var ended = await Task.WhenAny(closed.Task, Task.Delay(StatsInterval, ct)).ConfigureAwait(false);
                            if (ended == closed.Task) break;
                            ct.ThrowIfCancellationRequested();
                            Log.Write($"Akış: {session.PacketsSent} paket, {session.PacketsRetransmitted} yeniden gönderim, " +
                                      $"{source.Underruns} yetersizlik, tampon {source.BufferedFrames * 1000 / AirPlaySession.SampleRate} ms");
                        }
                        (reason, closeDetail) = await closed.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // TEARDOWN, using bloğu oturumu kapatmadan gitmeli; yoksa HomePod bir süre meşgul kalır.
                        DetachLocalOutput();
                        _session = null;
                        await session.StopAsync().ConfigureAwait(false);
                        throw;
                    }
                    if (reason == SessionCloseReason.DeviceClosed && DateTime.UtcNow - startedAt > MinimumStableUptime)
                    {
                        SetState(BridgeState.TakenOver, "HomePod başka bir cihaza geçti");
                        return;
                    }
                    Log.Write($"Oturum koptu: {reason} {closeDetail}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await StopSessionAsync().ConfigureAwait(false);
                    return;
                }
                catch (FirewallBlockedException ex)
                {
                    SetState(BridgeState.NeedsFirewall, ex.Message);
                    return;
                }
                catch (PairingRejectedException ex)
                {
                    SetState(BridgeState.Failed, ex.Message);
                    return;
                }
                catch (Exception ex)
                {
                    Log.Write($"Bağlantı hatası: {ex.GetType().Name}: {ex.Message}");
                }

                _session = null;
                DetachLocalOutput();
                int wait = BackoffSeconds[Math.Min(attempt++, BackoffSeconds.Length - 1)];
                SetState(BridgeState.Reconnecting, $"{wait} sn içinde yeniden denenecek");
                await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            await StopSessionAsync().ConfigureAwait(false);
            DetachLocalOutput();
            source.DeviceActive -= OnCaptureDevice;
            _source = null;
            _capturedDeviceId = null;
            source.Dispose();
            if (State is BridgeState.Connecting or BridgeState.Streaming or BridgeState.Reconnecting)
                SetState(BridgeState.Idle);
        }
    }

    private async Task StopSessionAsync()
    {
        var session = _session;
        _session = null;
        if (session != null) await session.StopAsync().ConfigureAwait(false);
    }

    /// <summary>DHCP adresi değişmiş olabileceğinden yeniden denemelerde mDNS ile güncel adresi ararız.</summary>
    private static async Task<AirPlayDevice> ResolveAsync(AirPlayDevice device, bool refresh, CancellationToken ct)
    {
        if (!refresh) return device;
        var devices = await MdnsBrowser.BrowseAsync(BrowseWindow, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return devices.FirstOrDefault(d => d.Id == device.Id) ?? device;
    }

    // ---------------------------------------------------------------- ses seviyesi ve hoparlör

    private void OnCaptureDevice(string deviceId)
    {
        _capturedDeviceId = deviceId;
        if (_session != null) AttachLocalOutput();
    }

    private void AttachLocalOutput()
    {
        if (!_settings.MuteLocal || _capturedDeviceId is not { } deviceId || _volume < 0) return;
        Task.Run(() =>
        {
            try
            {
                _localOutput.Attach(deviceId, _volume);
                _localAttached = true;
            }
            catch (Exception ex)
            {
                Log.Write($"Hoparlör kontrolü kurulamadı: {ex.Message}");
            }
        }).Wait();
    }

    private void DetachLocalOutput()
    {
        if (!_localAttached) return;
        _localAttached = false;
        Task.Run(_localOutput.Detach).Wait();
    }

    public void SetMuteLocal(bool mute)
    {
        _settings.MuteLocal = mute;
        _settings.Save();
        if (State != BridgeState.Streaming) return;
        if (mute) AttachLocalOutput();
        else DetachLocalOutput();
    }

    public Task SetVolumeAsync(double percent) => ApplyVolumeAsync(percent, fromWindows: false);

    private async Task ApplyVolumeAsync(double percent, bool fromWindows)
    {
        percent = Math.Clamp(percent, 0, 100);
        _volume = percent;
        Changed?.Invoke();

        if (!fromWindows && _localAttached)
            await Task.Run(() => _localOutput.Reflect(percent)).ConfigureAwait(false);

        // Kaydırıcı sürüklenirken istek birikmesin; en son değer her zaman gönderilir.
        _lastVolumeSent = DateTime.UtcNow;
        if (!await _volumeGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            double sent;
            do
            {
                sent = _volume;
                if (_session is { } session) await session.SetVolumeAsync(sent).ConfigureAwait(false);
            } while (Math.Abs(sent - _volume) > 0.01);
        }
        catch (Exception ex)
        {
            Log.Write($"Ses seviyesi gönderilemedi: {ex.Message}");
        }
        finally
        {
            _volumeGate.Release();
        }
    }

    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // Uykudan sonra oturum ölü olur; beklemeden yeni oturum için kapatıyoruz.
        Log.Write("Uykudan dönüldü");
        _session?.Dispose();
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerChanged;
        try { DisconnectAsync().Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException ex) { Log.Write($"Kapanış: {ex.InnerException?.Message}"); }
        _localOutput.Dispose();
    }
}
