using System.Numerics;
using System.Security.Cryptography;
using PodBridge.Audio;
using PodBridge.Protocol;
using PodBridge.Ui;

namespace PodBridge;

/// <summary>Unit tests that need no hardware: PodBridge --selftest</summary>
internal static class SelfTest
{
    private static int _failures;

    public static int Run()
    {
        AlacMatchesBitLayout();
        BPlistRoundTrip();
        Tlv8Fragments();
        SrpAgreesWithServer();
        SecureFramesRoundTrip();
        ResamplerPreservesTone();
        MdnsParsesResponse();
        LatencyPresets();
        StatusEndpoint();
        Console.WriteLine(_failures == 0 ? "ALL TESTS PASSED" : $"{_failures} TESTS FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok)
    {
        Console.WriteLine($"{(ok ? "pass" : "FAIL")} {name}");
        if (!ok) _failures++;
    }

    private static void AlacMatchesBitLayout()
    {
        var samples = new short[704];
        var random = new Random(7);
        for (int i = 0; i < samples.Length; i++) samples[i] = (short)random.Next(short.MinValue, short.MaxValue);

        var bits = new List<bool>();
        void Put(uint value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add((value >> i & 1) != 0);
        }
        Put(1, 3); Put(0, 4); Put(0, 12); Put(0, 1); Put(0, 2); Put(1, 1);
        foreach (short s in samples) Put((ushort)s, 16);
        Put(7, 3);
        while (bits.Count % 8 != 0) bits.Add(false);
        var expected = new byte[bits.Count / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) expected[i / 8] |= (byte)(0x80 >> (i % 8));

        var actual = new byte[Alac.FrameSize(samples.Length)];
        int written = Alac.Encode(samples, actual);
        Check("ALAC bit layout", written == expected.Length && actual.AsSpan().SequenceEqual(expected));
    }

    private static void BPlistRoundTrip()
    {
        var encoded = BPlist.Encode(new Dictionary<string, object>
        {
            ["name"] = "Café Speaker",
            ["port"] = 53180,
            ["large"] = 3_000_000_000L,
            ["flag"] = true,
            ["key"] = new byte[] { 1, 2, 3 },
            ["streams"] = new List<object> { new Dictionary<string, object> { ["type"] = 96 } },
        });
        var decoded = BPlist.Decode(encoded) as Dictionary<string, object?>;
        var stream = (decoded?["streams"] as List<object?>)?[0] as Dictionary<string, object?>;
        Check("bplist round trip",
              decoded?["name"] as string == "Café Speaker" && decoded["port"] as long? == 53180
              && decoded["large"] as long? == 3_000_000_000L && decoded["flag"] as bool? == true
              && (decoded["key"] as byte[])?.Length == 3 && stream?["type"] as long? == 96);

        bool rejected = false;
        try { BPlist.Decode(new byte[64]); }
        catch (FormatException) { rejected = true; }
        Check("malformed bplist rejected", rejected);
    }

    private static void Tlv8Fragments()
    {
        var big = RandomNumberGenerator.GetBytes(384);
        var decoded = Tlv8.Decode(Tlv8.Encode((Tlv8.State, [3]), (Tlv8.PublicKey, big), (Tlv8.Proof, new byte[64])));
        Check("TLV8 fragmentation", decoded[Tlv8.PublicKey].AsSpan().SequenceEqual(big) && decoded[Tlv8.State][0] == 3);
    }

    private static void SrpAgreesWithServer()
    {
        // Computes the server side here and checks that both sides reach the same key.
        var salt = RandomNumberGenerator.GetBytes(16);
        var inner = SHA512.HashData("Pair-Setup:3939"u8);
        var x = new BigInteger(SHA512.HashData([.. salt, .. inner]), isUnsigned: true, isBigEndian: true);
        var verifier = BigInteger.ModPow(SrpClient.G, x, SrpClient.N);
        var serverSecret = new BigInteger(RandomNumberGenerator.GetBytes(32), isUnsigned: true, isBigEndian: true);

        static byte[] Pad(BigInteger v)
        {
            var raw = v.ToByteArray(isUnsigned: true, isBigEndian: true);
            var padded = new byte[384];
            raw.CopyTo(padded, 384 - raw.Length);
            return padded;
        }
        var k = new BigInteger(SHA512.HashData([.. Pad(SrpClient.N), .. Pad(SrpClient.G)]), isUnsigned: true, isBigEndian: true);
        var b = (k * verifier + BigInteger.ModPow(SrpClient.G, serverSecret, SrpClient.N)) % SrpClient.N;

        var client = new SrpClient();
        bool processed = client.Process("3939", salt, b.ToByteArray(isUnsigned: true, isBigEndian: true));
        var a = new BigInteger(client.PublicA, isUnsigned: true, isBigEndian: true);
        var u = new BigInteger(SHA512.HashData([.. Pad(a), .. Pad(b)]), isUnsigned: true, isBigEndian: true);
        var s = BigInteger.ModPow(a * BigInteger.ModPow(verifier, u, SrpClient.N) % SrpClient.N, serverSecret, SrpClient.N);
        var expectedKey = SHA512.HashData(s.ToByteArray(isUnsigned: true, isBigEndian: true));
        Check("SRP shared key", processed && client.SessionKey.AsSpan().SequenceEqual(expectedKey));
    }

    private static void SecureFramesRoundTrip()
    {
        var keyA = RandomNumberGenerator.GetBytes(32);
        var keyB = RandomNumberGenerator.GetBytes(32);
        using var sender = new SecureFrames(keyA, keyB);
        using var receiver = new SecureFrames(keyB, keyA);
        var message = RandomNumberGenerator.GetBytes(3000);
        var wire = sender.Seal(message);
        var first = receiver.Open(wire.AsSpan(0, 1500));
        var second = receiver.Open(wire.AsSpan(1500));
        Check("encrypted frames, split reads", first.Concat(second).SequenceEqual(message));

        bool rejected = false;
        var tampered = sender.Seal("merhaba"u8);
        tampered[4] ^= 1;
        try { receiver.Open(tampered); }
        catch (CryptographicException) { rejected = true; }
        Check("tampered frame rejected", rejected);
    }

    private static void ResamplerPreservesTone()
    {
        const double InputRate = 48000, OutputRate = 44100, Hz = 1000;
        var resampler = new SincResampler(InputRate, OutputRate);
        var output = new float[2 * 44100];
        int produced = 0;
        for (int i = 0; i < 48000; i++)
        {
            float sample = (float)Math.Sin(2 * Math.PI * Hz * i / InputRate);
            resampler.Push(sample, sample);
            if (i % 480 == 479)
                produced += resampler.Pull(output.AsSpan(produced * 2), InputRate / OutputRate);
        }

        // Away from the start and end transients, separates 1 kHz power from everything else.
        int start = 4410, count = 22050;
        double re = 0, im = 0, power = 0;
        for (int i = start; i < start + count; i++)
        {
            double value = output[i * 2];
            double angle = 2 * Math.PI * Hz * i / OutputRate;
            re += value * Math.Cos(angle);
            im += value * Math.Sin(angle);
            power += value * value;
        }
        double tone = 2 * (re * re + im * im) / count;
        double residual = Math.Max(power - tone, 1e-12);
        double snr = 10 * Math.Log10(tone / residual);
        double amplitude = 2 * Math.Sqrt(re * re + im * im) / count;
        Console.WriteLine($"       resampling: {produced} frames, amplitude {amplitude:F4}, SNR {snr:F0} dB");
        Check("resampling", produced is > 43000 and < 44200 && Math.Abs(amplitude - 1) < 0.01 && snr > 80);
    }

    private static void StatusEndpoint()
    {
        using var server = new StatusServer(() => new { streaming = true, latencyMs = 420 }, port: 0);
        string Get(string path, string host)
        {
            using var tcp = new System.Net.Sockets.TcpClient("127.0.0.1", server.Port);
            var stream = tcp.GetStream();
            stream.Write(System.Text.Encoding.ASCII.GetBytes($"GET {path} HTTP/1.1\r\nHost: {host}\r\n\r\n"));
            return new StreamReader(stream).ReadToEnd();
        }
        var ok = Get("/status", $"127.0.0.1:{server.Port}");
        Check("status endpoint", ok.StartsWith("HTTP/1.1 200") && ok.Contains("\"latencyMs\":420") && !ok.Contains("Access-Control"));
        Check("status endpoint rejects foreign Host", Get("/status", "evil.example:80").StartsWith("HTTP/1.1 403"));
    }

    private static void LatencyPresets()
    {
        L.Current = UiLanguage.English;
        var settings = new Settings { LowBufferMs = 100, Latency = LatencyMode.Low };
        Check("preset buffer frames", settings.LatencyFrames() == 4410);
        Check("latency estimate at measured points",
              Settings.EstimateEndToEndMs(50) == 270 && Settings.EstimateEndToEndMs(120) == 420
              && Settings.EstimateEndToEndMs(200) == 570 && Settings.EstimateEndToEndMs(400) == 838);
        Check("latency estimate between and above points",
              Settings.EstimateEndToEndMs(160) == 495 && Settings.EstimateEndToEndMs(1000) == 1438);
        settings.Latency = LatencyMode.Stable;
        Check("latency label", settings.LatencyLabel() == "about 1.4 s");
        L.Current = UiLanguage.Turkish;
        Check("Turkish latency label", settings.LatencyLabel() == "yaklaşık 1,4 sn");
        L.Current = UiLanguage.English;
    }

    private static void MdnsParsesResponse()
    {
        using var ms = new MemoryStream();
        void Name(string name)
        {
            foreach (var label in name.Split('.'))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(label);
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes);
            }
            ms.WriteByte(0);
        }
        void Record(string name, int type, byte[] data)
        {
            Name(name);
            ms.Write([(byte)(type >> 8), (byte)type, 0, 1, 0, 0, 0, 120, (byte)(data.Length >> 8), (byte)data.Length]);
            ms.Write(data);
        }
        static byte[] EncodedName(string name)
        {
            using var inner = new MemoryStream();
            foreach (var label in name.Split('.'))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(label);
                inner.WriteByte((byte)bytes.Length);
                inner.Write(bytes);
            }
            inner.WriteByte(0);
            return inner.ToArray();
        }

        const string Instance = "Café Speaker._airplay._tcp.local";
        ms.Write([0, 0, 0x84, 0, 0, 0, 0, 4, 0, 0, 0, 0]);
        Record("_airplay._tcp.local", 12, EncodedName(Instance));
        Record(Instance, 33, [0, 0, 0, 0, 0x1B, 0x58, .. EncodedName("yatak.local")]);
        var model = "model=AudioAccessory5,1"u8.ToArray();
        var id = "deviceid=AA:BB:CC:DD:EE:01"u8.ToArray();
        Record(Instance, 16, [(byte)model.Length, .. model, (byte)id.Length, .. id]);
        Record("yatak.local", 1, [192, 168, 1, 50]);

        var device = MdnsBrowser.Parse(ms.ToArray()).SingleOrDefault();
        Check("mDNS reply", device is { Name: "Café Speaker", Address: "192.168.1.50", Port: 7000, IsHomePod: true });
    }
}
