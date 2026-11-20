using System.Diagnostics;
using System.Net;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PodBridge.Audio;
using PodBridge.Protocol;

namespace PodBridge;

/// <summary>End-to-end latency measurement: plays clicks on the Windows output and listens for them from the HomePod with the microphone.
/// Microphone data is only searched for peaks in memory and never recorded.
/// PodBridge --measure --ip 192.168.1.50 --latency 8820 --volume 25</summary>
internal static class LatencyMeter
{
    private const int Clicks = 12;
    private const double ClickHz = 2000;
    private const int ClickIntervalMs = 1250;

    public static async Task<int> RunAsync(string[] args)
    {
        var ip = IPAddress.Parse(Program.Option(args, "--ip") ?? throw new ArgumentException("--ip gerekli"));
        int latency = int.Parse(Program.Option(args, "--latency") ?? "8820");
        double volume = double.Parse(Program.Option(args, "--volume") ?? "25");
        void Print(string line) => Console.WriteLine(line);

        using var enumerator = new MMDeviceEnumerator();
        using var output = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        using var microphone = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        Print($"Output: {output.FriendlyName} / Microphone: {microphone.FriendlyName}");

        float savedVolume = output.AudioEndpointVolume.MasterVolumeLevelScalar;
        bool savedMute = output.AudioEndpointVolume.Mute;
        output.AudioEndpointVolume.Mute = true;
        try
        {
            using var loopback = new LoopbackSource(_ => { });
            using var session = new AirPlaySession(ip, 7000, new SessionOptions("PodBridge", "AA:BB:CC:11:22:33", latency), _ => { });
            await session.ConnectAsync(CancellationToken.None);
            session.StartStreaming(loopback);
            await session.SetVolumeAsync(volume);
            Print($"Latency reported by the HomePod: {session.ReceiverLatencyMs} ms, requested buffer: {latency * 1000 / AirPlaySession.SampleRate} ms");
            await Task.Delay(1500);

            var results = await MeasureAsync(output, microphone, loopback, Print);
            if (results.Count == 0)
            {
                Print("RESULT: the microphone did not hear the clicks (the HomePod may be too far away).");
            }
            else
            {
                results.Sort();
                Print($"RESULT: end-to-end median {results[results.Count / 2]:F0} ms, min {results[0]:F0}, max {results[^1]:F0} ({results.Count}/{Clicks} clicks)");
            }
            Print($"Underruns: {loopback.Underruns}, retransmits: {session.PacketsRetransmitted}, buffer fill: {loopback.BufferedFrames * 1000 / AirPlaySession.SampleRate} ms");
            await session.StopAsync();
            return results.Count > 0 ? 0 : 4;
        }
        finally
        {
            output.AudioEndpointVolume.MasterVolumeLevelScalar = savedVolume;
            output.AudioEndpointVolume.Mute = savedMute;
        }
    }

    /// <summary>Diagnostics: lists active output devices and which app is playing where. Changes nothing.</summary>
    public static int PrintAudioStatus()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var fallback = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
        {
            float peak = 0;
            for (int i = 0; i < 30; i++)
            {
                peak = Math.Max(peak, device.AudioMeterInformation.MasterPeakValue);
                Thread.Sleep(50);
            }
            string marker = device.ID == fallback.ID ? " [DEFAULT]" : "";
            Console.WriteLine($"{device.FriendlyName}{marker}: peak {peak:F3}, volume {device.AudioEndpointVolume.MasterVolumeLevelScalar * 100:F0}%, muted={device.AudioEndpointVolume.Mute}");
            var sessions = device.AudioSessionManager.Sessions;
            for (int i = 0; i < sessions.Count; i++)
            {
                var session = sessions[i];
                if (session.State != NAudio.CoreAudioApi.Interfaces.AudioSessionState.AudioSessionStateActive) continue;
                string name;
                try { name = Process.GetProcessById((int)session.GetProcessID).ProcessName; }
                catch (ArgumentException) { name = "?"; }
                Console.WriteLine($"    {name}: peak {session.AudioMeterInformation.MasterPeakValue:F3}");
            }
        }
        return 0;
    }

    /// <summary>Streams whatever is playing with a series of buffer sizes so the smallest clean one can be found by ear.
    /// The short silence between sections makes them easy to count.
    /// PodBridge --sweep --ip 192.168.1.50 --values 200,150,120,100,85 --seconds 15</summary>
    public static async Task<int> SweepAsync(string[] args)
    {
        var ip = IPAddress.Parse(Program.Option(args, "--ip") ?? throw new ArgumentException("--ip gerekli"));
        int seconds = int.Parse(Program.Option(args, "--seconds") ?? "15");
        var values = (Program.Option(args, "--values") ?? "200,150,120,100,85").Split(',').Select(int.Parse).ToList();

        using var enumerator = new MMDeviceEnumerator();
        using var output = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        bool savedMute = output.AudioEndpointVolume.Mute;
        double? volume = Program.Option(args, "--volume") is { } v ? double.Parse(v) : null;
        try
        {
            // Same order as the app: capture starts first, the speakers are muted after.
            using var loopback = new LoopbackSource(Log.Write);
            await Task.Delay(500);
            Log.Write($"Peak captured before muting: {loopback.Peak:F3}");
            output.AudioEndpointVolume.Mute = true;
            for (int i = 0; i < values.Count; i++)
            {
                int frames = values[i] * AirPlaySession.SampleRate / 1000;
                using var session = new AirPlaySession(ip, 7000, new SessionOptions("PodBridge", "AA:BB:CC:11:22:33", frames), _ => { });
                await session.ConnectAsync(CancellationToken.None);
                long underrunsBefore = loopback.Underruns;
                session.StartStreaming(loopback);
                if (volume is { } target) await session.SetVolumeAsync(target);
                double? homePodVolume = await session.GetVolumePercentAsync();
                float loudest = 0;
                for (int tick = 0; tick < seconds * 10; tick++)
                {
                    await Task.Delay(100);
                    loudest = Math.Max(loudest, loopback.Peak);
                }
                Log.Write($"Section {i + 1}: buffer {values[i]} ms, HomePod volume {homePodVolume:F0}%, captured peak {loudest:F3}, " +
                                  $"{session.PacketsSent} packets, {session.PacketsRetransmitted} retransmits, {loopback.Underruns - underrunsBefore} underruns");
                await session.StopAsync();
                await Task.Delay(2500);
            }
            return 0;
        }
        finally
        {
            output.AudioEndpointVolume.Mute = savedMute;
        }
    }

    /// <summary>Plays a tone with the speakers muted and checks whether the endpoint volume changes the loopback level.
    /// Because the Windows slider is tied to the HomePod, the level must be independent of it.</summary>
    public static async Task<int> ProbeVolumeAsync()
    {
        using var enumerator = new MMDeviceEnumerator();
        using var output = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
        var endpoint = output.AudioEndpointVolume;
        float savedVolume = endpoint.MasterVolumeLevelScalar;
        bool savedMute = endpoint.Mute;
        endpoint.Mute = true;
        try
        {
            using var loopback = new LoopbackSource(_ => { });
            var mix = output.AudioClient.MixFormat;
            var tone = new SignalGenerator(mix.SampleRate, Math.Min(mix.Channels, 2)) { Frequency = 1000, Gain = 0.5 };
            using var player = new WasapiOut(output, AudioClientShareMode.Shared, true, 50);
            player.Init(tone.ToWaveProvider());
            player.Play();
            foreach (float level in new[] { 1.0f, 0.5f, 0.1f, 0.02f })
            {
                endpoint.MasterVolumeLevelScalar = level;
                await Task.Delay(600);
                var drain = new short[704];
                for (int i = 0; i < 10; i++) loopback.Read(drain);
                Console.WriteLine($"Volume {level * 100:F0}%: captured peak {loopback.Peak:F3}");
            }
            player.Stop();
            return 0;
        }
        finally
        {
            endpoint.MasterVolumeLevelScalar = savedVolume;
            endpoint.Mute = savedMute;
        }
    }

    private static async Task<List<double>> MeasureAsync(MMDevice output, MMDevice microphone, LoopbackSource loopback, Action<string> print)
    {
        var levels = new List<(long Ticks, float Peak)>();
        using var capture = new WasapiCapture(microphone, true, 10);
        var micFormat = capture.WaveFormat;
        capture.DataAvailable += (_, e) =>
        {
            long now = Stopwatch.GetTimestamp();
            int bytesPerSample = micFormat.BitsPerSample / 8;
            int frameBytes = bytesPerSample * micFormat.Channels;
            int frames = e.BytesRecorded / frameBytes;
            // Measure energy at the click frequency with Goertzel so room noise peaks do not mislead us.
            int window = micFormat.SampleRate / 500;
            double coefficient = 2 * Math.Cos(2 * Math.PI * ClickHz / micFormat.SampleRate);
            for (int start = 0; start + window <= frames; start += window)
            {
                double s1 = 0, s2 = 0;
                for (int f = start; f < start + window; f++)
                {
                    double sample = bytesPerSample == 4
                        ? BitConverter.ToSingle(e.Buffer, f * frameBytes)
                        : BitConverter.ToInt16(e.Buffer, f * frameBytes) / 32768.0;
                    double s0 = sample + coefficient * s1 - s2;
                    s2 = s1;
                    s1 = s0;
                }
                float energy = (float)(Math.Sqrt(Math.Max(0, s1 * s1 + s2 * s2 - coefficient * s1 * s2)) * 2 / window);
                long age = (long)((double)(frames - start) / micFormat.SampleRate * Stopwatch.Frequency);
                lock (levels) levels.Add((now - age, energy));
            }
        };
        capture.StartRecording();

        var mixFormat = output.AudioClient.MixFormat;
        var click = new SignalGenerator(mixFormat.SampleRate, Math.Min(mixFormat.Channels, 2))
        {
            Type = SignalGeneratorType.Sin,
            Frequency = ClickHz,
            Gain = 0.9,
        };
        var gate = new GatedProvider(click);
        using var player = new WasapiOut(output, AudioClientShareMode.Shared, true, 20);
        player.Init(gate);
        player.Play();
        await Task.Delay(800);

        // Measure the quiet room level.
        float ambient;
        lock (levels) ambient = levels.Count > 0 ? levels.Max(l => l.Peak) : 0;
        float threshold = Math.Max(ambient * 2.5f, 0.002f);
        print($"Ambient peak {ambient:F4}, threshold {threshold:F4}");

        var clickTimes = new List<long>();
        for (int i = 0; i < Clicks; i++)
        {
            clickTimes.Add(gate.Fire(TimeSpan.FromMilliseconds(60)));
            await Task.Delay(ClickIntervalMs);
        }
        capture.StopRecording();
        player.Stop();

        var results = new List<double>();
        lock (levels)
        {
            foreach (long t in clickTimes)
            {
                long limit = t + (long)(ClickIntervalMs / 1000.0 * Stopwatch.Frequency);
                var hit = levels.FirstOrDefault(l => l.Ticks > t && l.Ticks < limit && l.Peak >= threshold);
                if (hit.Ticks != 0)
                {
                    double ms = (hit.Ticks - t) * 1000.0 / Stopwatch.Frequency;
                    results.Add(ms);
                    print($"  click: {ms:F0} ms (peak {hit.Peak:F3})");
                }
                else
                {
                    print("  click: not heard");
                }
            }
        }
        return results;
    }

    /// <summary>Lets sound through briefly on request; waits up to one device buffer to return when the first sample went out.</summary>
    private sealed class GatedProvider : IWaveProvider
    {
        private readonly IWaveProvider _inner;
        private long _remainingBytes;
        private long _firedAt;

        public GatedProvider(ISampleProvider source)
        {
            _inner = source.ToWaveProvider();
            WaveFormat = _inner.WaveFormat;
        }

        public WaveFormat WaveFormat { get; }

        public long Fire(TimeSpan duration)
        {
            _firedAt = 0;
            Interlocked.Exchange(ref _remainingBytes, (long)(duration.TotalSeconds * WaveFormat.AverageBytesPerSecond));
            SpinWait.SpinUntil(() => Volatile.Read(ref _firedAt) != 0, 200);
            return Volatile.Read(ref _firedAt);
        }

        public int Read(byte[] buffer, int offset, int count)
        {
            int read = _inner.Read(buffer, offset, count);
            long remaining = Interlocked.Read(ref _remainingBytes);
            if (remaining <= 0)
            {
                Array.Clear(buffer, offset, read);
                return read;
            }
            if (Volatile.Read(ref _firedAt) == 0) Volatile.Write(ref _firedAt, Stopwatch.GetTimestamp());
            int keep = (int)Math.Min(remaining, read);
            Array.Clear(buffer, offset + keep, read - keep);
            Interlocked.Add(ref _remainingBytes, -keep);
            return read;
        }
    }
}
