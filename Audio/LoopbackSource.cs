using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using NAudio.Wave;
using PodBridge.Protocol;

namespace PodBridge.Audio;

/// <summary>Captures what goes to the default output device with WASAPI loopback and converts it to 44100 Hz stereo 16-bit.
/// No driver needed. Restarts itself when the device changes or disappears.</summary>
public sealed class LoopbackSource : IPcmSource, IDisposable
{
    private const int RingFrames = AirPlaySession.SampleRate;
    private const int TargetFrames = 1323;          // 30 ms: covers capture (10 ms) and send (8 ms) jitter
    private const int MaxFrames = 8820;             // anything above 200 ms (sleep, dropouts) is discarded
    private const int ExcessFrames = 1764;          // also discarded when the average stays 40 ms above target
    private const double MaxRateTrim = 0.003;       // at most ±0.3%; the pitch change is inaudible
    private const double TrimPerFrame = 2.5e-6;
    private const long BufferDuration100Ns = 200_000;

    private readonly short[] _ring = new short[RingFrames * 2];
    private long _written;
    private long _read;
    private volatile bool _priming = true;
    private volatile bool _skipBacklog;

    private readonly Action<string> _log;
    private readonly Thread _thread;
    private readonly CancellationTokenSource _stop = new();
    private readonly ManualResetEventSlim _deviceChanged = new(false);
    private uint _noise = 0x9E3779B9;

    public long BufferedFrames => Volatile.Read(ref _written) - Volatile.Read(ref _read);
    public long Underruns => Volatile.Read(ref _underruns);
    private long _underruns;
    /// <summary>Times buffered audio was dropped to keep the latency bounded; each one is a small audible jump.</summary>
    public long Skips => Volatile.Read(ref _skips);
    private long _skips;

    /// <summary>Raised with the device ID whenever the captured device changes.</summary>
    public event Action<string>? DeviceActive;

    /// <summary>Peak level over the last 100 ms (0..1), for UI and diagnostics.</summary>
    public float Peak { get; private set; }

    public LoopbackSource(Action<string> log)
    {
        _log = log;
        _thread = new Thread(CaptureLoop) { IsBackground = true, Name = "Audio capture", Priority = ThreadPriority.Highest };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    // ---------------------------------------------------------------- consumer side

    public void Read(Span<short> interleaved)
    {
        int wanted = interleaved.Length / 2;
        long read = Volatile.Read(ref _read);
        long written = Volatile.Read(ref _written);
        if (_skipBacklog)
        {
            _skipBacklog = false;
            if (written - TargetFrames > read) Interlocked.Increment(ref _skips);
            read = Math.Max(read, written - TargetFrames);
        }
        long available = written - read;

        if (_priming)
        {
            if (available < TargetFrames)
            {
                Volatile.Write(ref _read, read);
                interleaved.Clear();
                return;
            }
            _priming = false;
        }
        if (available < wanted)
        {
            // On underrun, play silence until the target fill is back instead of stuttering through small fragments.
            Interlocked.Increment(ref _underruns);
            _priming = true;
            Volatile.Write(ref _read, read);
            interleaved.Clear();
            return;
        }

        int start = (int)(read % RingFrames) * 2;
        int first = Math.Min(wanted * 2, _ring.Length - start);
        _ring.AsSpan(start, first).CopyTo(interleaved);
        if (first < wanted * 2) _ring.AsSpan(0, wanted * 2 - first).CopyTo(interleaved[first..]);
        Volatile.Write(ref _read, read + wanted);
    }

    // ---------------------------------------------------------------- producer side

    private sealed class EndpointWatcher(ManualResetEventSlim signal) : IMMNotificationClient
    {
        public void OnDefaultDeviceChanged(DataFlow flow, Role role, string defaultDeviceId)
        {
            if (flow == DataFlow.Render && role == Role.Multimedia) signal.Set();
        }
        public void OnDeviceStateChanged(string deviceId, DeviceState newState) { }
        public void OnDeviceAdded(string pwstrDeviceId) { }
        public void OnDeviceRemoved(string deviceId) { }
        public void OnPropertyValueChanged(string pwstrDeviceId, PropertyKey key) { }
    }

    private void CaptureLoop()
    {
        using var mmcss = NativeTiming.EnterProAudio();
        using var enumerator = new MMDeviceEnumerator();
        var watcher = new EndpointWatcher(_deviceChanged);
        enumerator.RegisterEndpointNotificationCallback(watcher);
        string? lastError = null;
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    _deviceChanged.Reset();
                    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    CaptureFrom(device);
                    lastError = null;
                }
                catch (Exception ex)
                {
                    // Any failure here must restart capture, never end this thread: an exception escaping a background
                    // thread closes the whole app. Repeats of the same error (no output device, for example) are logged once.
                    if (ex.Message != lastError) _log($"Restarting capture: {ex.GetType().Name}: {ex.Message}");
                    lastError = ex.Message;
                    _stop.Token.WaitHandle.WaitOne(500);
                }
            }
        }
        finally
        {
            enumerator.UnregisterEndpointNotificationCallback(watcher);
        }
    }

    private void CaptureFrom(MMDevice device)
    {
        var client = device.AudioClient;
        var format = client.MixFormat;
        if (format.BitsPerSample != 32 || (format.Encoding != WaveFormatEncoding.IeeeFloat && format is not WaveFormatExtensible))
            throw new NotSupportedException($"Unexpected mix format: {format}");

        client.Initialize(AudioClientShareMode.Shared, AudioClientStreamFlags.Loopback, BufferDuration100Ns, 0, format, Guid.Empty);
        var capture = client.AudioCaptureClient;

        // When no app is playing, loopback produces no data and the timeline starves.
        // Writing silence to the device keeps the capture running continuously.
        using var keepAlive = new WasapiOut(device, AudioClientShareMode.Shared, false, 100);
        keepAlive.Init(new SilenceProvider(format));
        keepAlive.Play();

        int channels = format.Channels;
        double inputRate = format.SampleRate;
        var resampler = new SincResampler(inputRate, AirPlaySession.SampleRate);
        double nominalStep = inputRate / AirPlaySession.SampleRate;
        Span<float> output = new float[4096];
        double smoothedFill = TargetFrames;
        using var timer = new NativeTiming.PreciseTimer();

        client.Start();
        _log($"Capturing: {device.FriendlyName}, {format.SampleRate} Hz, {channels} channels");
        DeviceActive?.Invoke(device.ID);

        float peak = 0;
        int peakFrames = 0;
        try
        {
            while (!_stop.IsCancellationRequested && !_deviceChanged.IsSet)
            {
                int packet;
                while ((packet = capture.GetNextPacketSize()) > 0)
                {
                    IntPtr data = capture.GetBuffer(out int frames, out AudioClientBufferFlags flags);
                    bool silent = (flags & AudioClientBufferFlags.Silent) != 0;
                    unsafe
                    {
                        float* samples = (float*)data;
                        for (int i = 0; i < frames; i++, samples += channels)
                        {
                            float left = 0, right = 0;
                            if (!silent) Downmix(samples, channels, out left, out right);
                            resampler.Push(left, right);
                            float magnitude = MathF.Max(MathF.Abs(left), MathF.Abs(right));
                            if (magnitude > peak) peak = magnitude;
                        }
                    }
                    capture.ReleaseBuffer(frames);

                    peakFrames += frames;
                    if (peakFrames >= format.SampleRate / 10)
                    {
                        Peak = peak;
                        peak = 0;
                        peakFrames = 0;
                    }

                    long fill = Volatile.Read(ref _written) - Volatile.Read(ref _read);
                    smoothedFill += (fill - smoothedFill) * 0.02;
                    if (smoothedFill > TargetFrames + ExcessFrames)
                    {
                        // Rate trimming would take tens of seconds to drain this backlog; skip it instead of carrying the extra latency.
                        _skipBacklog = true;
                        smoothedFill = TargetFrames;
                    }
                    double trim = Math.Clamp((smoothedFill - TargetFrames) * TrimPerFrame, -MaxRateTrim, MaxRateTrim);
                    int produced;
                    while ((produced = resampler.Pull(output, nominalStep * (1 + trim))) > 0)
                        Enqueue(output[..(produced * 2)]);
                }
                timer.Wait(2);
            }
        }
        finally
        {
            client.Stop();
            client.Dispose();
            keepAlive.Stop();
        }
    }

    private static unsafe void Downmix(float* s, int channels, out float left, out float right)
    {
        const float Center = 0.7071f;
        switch (channels)
        {
            case 1:
                left = right = s[0];
                break;
            case 2:
            case 3:
                left = s[0];
                right = s[1];
                break;
            default:
                // Order is FL FR C LFE (B/S L R ...). LFE is dropped and gain is lowered to avoid clipping.
                float center = s[2] * Center;
                float backL = channels >= 6 ? s[4] * Center : 0;
                float backR = channels >= 6 ? s[5] * Center : 0;
                float sideL = channels >= 8 ? s[6] * Center : 0;
                float sideR = channels >= 8 ? s[7] * Center : 0;
                left = (s[0] + center + backL + sideL) * 0.5f;
                right = (s[1] + center + backR + sideR) * 0.5f;
                break;
        }
    }

    private void Enqueue(ReadOnlySpan<float> interleaved)
    {
        long written = Volatile.Read(ref _written);
        long read = Volatile.Read(ref _read);
        int frames = interleaved.Length / 2;

        // If the consumer has stalled, dropping old samples keeps latency bounded.
        // _read is only written by the consumer; here we just request a skip.
        if (written - read + frames > MaxFrames) _skipBacklog = true;

        for (int i = 0; i < frames; i++)
        {
            int slot = (int)((written + i) % RingFrames) * 2;
            _ring[slot] = ToPcm(interleaved[i * 2]);
            _ring[slot + 1] = ToPcm(interleaved[i * 2 + 1]);
        }
        Volatile.Write(ref _written, written + frames);
    }

    // TPDF dither when reducing to 16 bits; no noise is added to digital silence.
    private short ToPcm(float sample)
    {
        if (sample == 0) return 0;
        _noise ^= _noise << 13; _noise ^= _noise >> 17; _noise ^= _noise << 5;
        float a = (_noise & 0xFFFF) / 65536f;
        float b = (_noise >> 16) / 65536f;
        float value = sample * 32767f + (a - b);
        return (short)Math.Clamp(MathF.Round(value), short.MinValue, short.MaxValue);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _thread.Join(1000);
    }
}
