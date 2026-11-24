using System.Net;
using Microsoft.Win32;
using PodBridge.Audio;
using PodBridge.Protocol;
using PodBridge.Ui;

namespace PodBridge;

public enum BridgeState
{
    Idle,
    Connecting,
    Streaming,
    Reconnecting,
    /// <summary>Another device took over the HomePod; we do not take it back unless the user asks.</summary>
    TakenOver,
    NeedsFirewall,
    Failed,
}

/// <summary>Runs capture, the sessions and reconnects. A list entry may stand for several HomePods (a stereo pair);
/// each gets its own session, all on one timeline. Events arrive on background threads.</summary>
public sealed class BridgeEngine : IDisposable
{
    private static readonly int[] BackoffSeconds = [1, 2, 4, 8, 15, 30];
    private static readonly TimeSpan BrowseWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MinimumStableUptime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RecoveryWindow = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan StatsInterval = TimeSpan.FromSeconds(30);

    private readonly Settings _settings;
    private readonly LocalOutputControl _localOutput;
    private readonly object _gate = new();
    private CancellationTokenSource? _run;
    private Task _runTask = Task.CompletedTask;
    private volatile IReadOnlyList<AirPlaySession> _sessions = [];
    private LoopbackSource? _source;
    private string? _capturedDeviceId;
    private bool _localAttached;
    private double _volume = -1;
    private double? _volumeBeforeMute;
    private readonly SemaphoreSlim _volumeGate = new(1, 1);

    public BridgeState State { get; private set; }
    public AirPlayDevice? Device { get; private set; }
    public string Detail { get; private set; } = "";
    public double Volume => _volume;
    public bool HomePodMuted => _volumeBeforeMute != null;
    /// <summary>The level to show: while muted, the level the HomePod returns to.</summary>
    public double DisplayVolume => _volumeBeforeMute ?? Math.Max(_volume, 0);
    public long? ReceiverLatencyMs => _sessions.Count > 0 ? _sessions[0].ReceiverLatencyMs : null;
    public long PacketsRetransmitted => _sessions.Sum(s => s.PacketsRetransmitted);
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
        Log.Write($"State: {state} {detail}");
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

    private static IReadOnlyList<StreamMember> MembersOf(AirPlayDevice device) =>
        device.Members is { Count: > 0 } members ? members : [new StreamMember(device, ChannelMode.Stereo)];

    private async Task RunAsync(AirPlayDevice device, Task previous, CancellationToken ct)
    {
        try { await previous.ConfigureAwait(false); }
        catch (Exception ex) { Log.Write($"Previous session: {ex.Message}"); }
        if (ct.IsCancellationRequested) return;

        var source = new LoopbackSource(Log.Write);
        source.DeviceActive += OnCaptureDevice;
        _source = source;
        var members = MembersOf(device);
        bool group = members.Count > 1;
        if (group) Log.Write($"Group: {string.Join(", ", members.Select(m => $"{m.Device.Name} ({m.Channel})"))}");
        int attempt = 0;
        DateTime lastNetworkDrop = DateTime.MinValue;
        SetState(BridgeState.Connecting);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                DateTime startedAt = default;
                var sessions = new List<AirPlaySession>();
                try
                {
                    members = await ResolveAsync(members, attempt > 0, ct).ConfigureAwait(false);
                    if (!group)
                    {
                        device = members[0].Device;
                        Device = device;
                    }

                    var closed = new TaskCompletionSource<(SessionCloseReason Reason, string Detail)>(
                        TaskCreationOptions.RunContinuationsAsynchronously);
                    foreach (var member in members)
                    {
                        var options = new SessionOptions(Environment.MachineName, _settings.ClientId, _settings.LatencyFrames(),
                                                         _settings.NetworkPriority, member.Channel);
                        string prefix = group ? $"[{member.Device.Name}] " : "";
                        var session = new AirPlaySession(IPAddress.Parse(member.Device.Address), member.Device.Port, options,
                                                         line => Log.Write(prefix + line));
                        session.Closed += (reason, detail) => closed.TrySetResult((reason, prefix + detail));
                        sessions.Add(session);
                    }

                    // A group only plays once every HomePod in it is ready.
                    await Task.WhenAll(sessions.Select(s => s.ConnectAsync(ct))).ConfigureAwait(false);
                    _sessions = sessions;
                    _volumeBeforeMute = null;
                    var timeline = new StreamTimeline();
                    var fanout = new PacketFanout(source);
                    foreach (var session in sessions) session.StartStreaming(fanout, timeline);
                    startedAt = DateTime.UtcNow;

                    // The stream already plays at this point; a slow or failed volume query must not tear it down.
                    double? current = null;
                    try { current = await sessions[0].GetVolumePercentAsync().ConfigureAwait(false); }
                    catch (Exception ex) when (ex is TimeoutException or IOException) { Log.Write($"Could not read volume: {ex.Message}"); }
                    if (current is { } known) _volume = known;
                    else if (_volume < 0) _volume = 30;
                    // A group plays at one level: the others follow the first HomePod.
                    if (group) await SendVolumeAsync(sessions, _volume).ConfigureAwait(false);
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
                            Log.Write($"Stream: {sessions.Sum(s => s.PacketsSent)} packets, {sessions.Sum(s => s.PacketsRetransmitted)} retransmits, " +
                                      $"{source.Underruns} underruns, {source.Skips} capture skips, buffer {source.BufferedFrames * 1000 / AirPlaySession.SampleRate} ms");
                        }
                        (reason, closeDetail) = await closed.Task.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // TEARDOWN has to go out before the sessions are disposed, or the HomePods stay busy for a while.
                        DetachLocalOutput();
                        _sessions = [];
                        // Muted with the mute key: give the HomePods their volume back, or they start silent next time.
                        if (_volumeBeforeMute is { } restore)
                        {
                            _volumeBeforeMute = null;
                            _volume = restore;
                            try { await SendVolumeAsync(sessions, restore).WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); }
                            catch (Exception ex) { Log.Write($"Could not restore volume: {ex.Message}"); }
                        }
                        await StopAllAsync(sessions).ConfigureAwait(false);
                        throw;
                    }
                    // One HomePod of a group dropped: close the others properly too, then start over together.
                    _sessions = [];
                    await StopAllAsync(sessions).ConfigureAwait(false);

                    // After a network drop the HomePod still holds the old session for a while and may close the new one
                    // while it cleans up. That is not another device taking over, so keep retrying in that window.
                    bool recentDrop = DateTime.UtcNow - lastNetworkDrop < RecoveryWindow;
                    if (reason == SessionCloseReason.DeviceClosed && DateTime.UtcNow - startedAt > MinimumStableUptime && !recentDrop)
                    {
                        SetState(BridgeState.TakenOver, L.T("The HomePod switched to another device", "HomePod başka bir cihaza geçti"));
                        return;
                    }
                    if (reason != SessionCloseReason.DeviceClosed) lastNetworkDrop = DateTime.UtcNow;
                    Log.Write($"Session dropped: {reason} {closeDetail}{(recentDrop && reason == SessionCloseReason.DeviceClosed ? " (right after a network drop, retrying)" : "")}");
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await StopSessionsAsync().ConfigureAwait(false);
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
                    Log.Write($"Connection error: {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    foreach (var session in sessions) session.Dispose();
                }

                _sessions = [];
                DetachLocalOutput();
                int wait = BackoffSeconds[Math.Min(attempt++, BackoffSeconds.Length - 1)];
                SetState(BridgeState.Reconnecting, L.T($"retrying in {wait} s", $"{wait} sn içinde yeniden denenecek"));
                await Task.Delay(TimeSpan.FromSeconds(wait), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally
        {
            await StopSessionsAsync().ConfigureAwait(false);
            DetachLocalOutput();
            source.DeviceActive -= OnCaptureDevice;
            _source = null;
            _capturedDeviceId = null;
            source.Dispose();
            if (State is BridgeState.Connecting or BridgeState.Streaming or BridgeState.Reconnecting)
                SetState(BridgeState.Idle);
        }
    }

    private static Task StopAllAsync(IEnumerable<AirPlaySession> sessions) =>
        Task.WhenAll(sessions.Select(s => s.StopAsync()));

    private async Task StopSessionsAsync()
    {
        var sessions = _sessions;
        _sessions = [];
        await StopAllAsync(sessions).ConfigureAwait(false);
    }

    /// <summary>The DHCP address may have changed, so retries look the HomePods up again over mDNS.</summary>
    private static async Task<IReadOnlyList<StreamMember>> ResolveAsync(IReadOnlyList<StreamMember> members, bool refresh,
                                                                        CancellationToken ct)
    {
        if (!refresh) return members;
        var devices = await MdnsBrowser.BrowseAsync(BrowseWindow, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return [.. members.Select(m => m with { Device = devices.FirstOrDefault(d => d.Id == m.Device.Id) ?? m.Device })];
    }

    // ---------------------------------------------------------------- volume and speakers

    private void OnCaptureDevice(string deviceId)
    {
        _capturedDeviceId = deviceId;
        if (_sessions.Count > 0) AttachLocalOutput();
    }

    private void AttachLocalOutput()
    {
        if (_capturedDeviceId is not { } deviceId || _volume < 0) return;
        bool mute = _settings.MuteLocal;
        bool link = _settings.VolumeKeys;
        Task.Run(() =>
        {
            try
            {
                _localOutput.Attach(deviceId, _volume, mute, link);
                _localAttached = true;
            }
            catch (Exception ex)
            {
                Log.Write($"Could not take over the speakers: {ex.Message}");
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
        // The volume link stays either way; only whether the speakers are muted changes.
        DetachLocalOutput();
        AttachLocalOutput();
    }

    public Task SetVolumeAsync(double percent) => ApplyVolumeAsync(percent, fromWindows: false);

    /// <summary>Turns the link between the Windows volume and the HomePod on or off while streaming.</summary>
    public void SetVolumeKeys(bool enabled)
    {
        _settings.VolumeKeys = enabled;
        _settings.Save();
        if (State != BridgeState.Streaming) return;
        DetachLocalOutput();
        AttachLocalOutput();
    }

    /// <summary>A volume key: changes the HomePod by the same 2% step Windows uses. Starting from the muted level
    /// if the HomePod is muted, like Windows does, so the key also unmutes.</summary>
    public void StepVolume(int delta)
    {
        if (State != BridgeState.Streaming || _volume < 0) return;
        double from = _volumeBeforeMute ?? _volume;
        double target = Math.Clamp(Math.Round((from + delta) / 2) * 2, 0, 100);
        _ = ApplyVolumeAsync(target, fromWindows: false);
    }

    /// <summary>The mute key: silences the HomePod and brings back the previous level on the next press. The Windows
    /// volume is left alone, so a volume key press also unmutes, at the new level.</summary>
    public void ToggleHomePodMute()
    {
        if (State != BridgeState.Streaming || _volume < 0) return;
        if (_volumeBeforeMute is { } previous)
        {
            _ = ApplyVolumeAsync(previous, fromWindows: true);
            return;
        }
        double current = _volume;
        _ = ApplyVolumeAsync(0, fromWindows: true, muting: true);
        _volumeBeforeMute = current;
    }

    private static Task SendVolumeAsync(IEnumerable<AirPlaySession> sessions, double percent) =>
        Task.WhenAll(sessions.Select(s => s.SetVolumeAsync(percent)));

    private async Task ApplyVolumeAsync(double percent, bool fromWindows, bool muting = false)
    {
        if (!muting) _volumeBeforeMute = null;
        percent = Math.Clamp(percent, 0, 100);
        _volume = percent;
        Changed?.Invoke();

        if (!fromWindows && _localAttached)
            await Task.Run(() => _localOutput.Reflect(percent)).ConfigureAwait(false);

        // Do not pile up requests while the slider is dragged; the latest value is always sent.
        if (!await _volumeGate.WaitAsync(0).ConfigureAwait(false)) return;
        try
        {
            double sent;
            do
            {
                sent = _volume;
                await SendVolumeAsync(_sessions, sent).ConfigureAwait(false);
            } while (Math.Abs(sent - _volume) > 0.01);
        }
        catch (Exception ex)
        {
            Log.Write($"Could not send volume: {ex.Message}");
        }
        finally
        {
            _volumeGate.Release();
        }
    }

    private void OnPowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode != PowerModes.Resume) return;
        // After sleep the sessions are dead; close them now so new ones start without waiting.
        Log.Write("Resumed from sleep");
        foreach (var session in _sessions) session.Dispose();
    }

    public void Dispose()
    {
        SystemEvents.PowerModeChanged -= OnPowerChanged;
        try { DisconnectAsync().Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException ex) { Log.Write($"Shutdown: {ex.InnerException?.Message}"); }
        _localOutput.Dispose();
    }
}
