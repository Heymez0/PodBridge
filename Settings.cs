using System.Security.Cryptography;
using System.Text.Json;
using PodBridge.Protocol;
using PodBridge.Ui;

namespace PodBridge;

public enum LatencyMode
{
    Low,
    Balanced,
    Stable,
}

public sealed class LocalAudioSnapshot
{
    public string DeviceId { get; set; } = "";
    public float Volume { get; set; }
    public bool Muted { get; set; }
}

public sealed class Settings
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PodBridge");

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public string ClientId { get; set; } = NewClientId();
    public string? LastDeviceId { get; set; }
    public List<AirPlayDevice> KnownDevices { get; set; } = [];
    public const int MinBufferMs = 50;
    public const int MaxBufferMs = 2000;
    public const int DefaultLowBufferMs = 120;
    public const int DefaultBalancedBufferMs = 200;
    public const int DefaultStableBufferMs = 1000;

    public LatencyMode Latency { get; set; } = LatencyMode.Low;
    public UiLanguage Language { get; set; } = L.SystemDefault();
    public int LowBufferMs { get; set; } = DefaultLowBufferMs;
    public int BalancedBufferMs { get; set; } = DefaultBalancedBufferMs;
    public int StableBufferMs { get; set; } = DefaultStableBufferMs;
    public bool MuteLocal { get; set; } = true;
    public bool AutoConnect { get; set; }
    public bool FirstRunDone { get; set; }
    public bool AutoUpdate { get; set; } = true;
    public DateTime LastUpdateCheck { get; set; }

    /// <summary>Saved so the computer's speakers do not stay muted if the app crashes.</summary>
    public LocalAudioSnapshot? PendingRestore { get; set; }

    private static string NewClientId()
    {
        var bytes = RandomNumberGenerator.GetBytes(6);
        bytes[0] = (byte)(bytes[0] & 0xFE | 0x02);  // Locally administered MAC format
        return string.Join(':', bytes.Select(b => b.ToString("X2")));
    }

    public int BufferMs(LatencyMode mode) => mode switch
    {
        LatencyMode.Low => LowBufferMs,
        LatencyMode.Balanced => BalancedBufferMs,
        _ => StableBufferMs,
    };

    public int LatencyFrames() => BufferMs(Latency) * AirPlaySession.SampleRate / 1000;

    public string LatencyLabel()
    {
        int estimate = EstimateEndToEndMs(BufferMs(Latency));
        return estimate < 1000
            ? L.T($"about {(estimate + 5) / 10 * 10} ms", $"yaklaşık {(estimate + 5) / 10 * 10} ms")
            : L.T($"about {estimate / 1000.0:F1} s", $"yaklaşık {estimate / 1000.0:F1} sn".Replace('.', ','));
    }

    /// <summary>Linear interpolation between microphone measurements on a HomePod mini over Wi-Fi.
    /// Above the last measurement it is assumed to grow one-to-one with the buffer.</summary>
    public static int EstimateEndToEndMs(int bufferMs)
    {
        (int Buffer, int EndToEnd)[] measured = [(50, 270), (120, 420), (200, 570), (400, 838)];
        if (bufferMs >= measured[^1].Buffer) return measured[^1].EndToEnd + bufferMs - measured[^1].Buffer;
        for (int i = 1; i < measured.Length; i++)
        {
            if (bufferMs > measured[i].Buffer) continue;
            var (b0, e0) = measured[i - 1];
            var (b1, e1) = measured[i];
            return e0 + (bufferMs - b0) * (e1 - e0) / (b1 - b0);
        }
        return measured[0].EndToEnd;
    }

    private void ClampBuffers()
    {
        LowBufferMs = Math.Clamp(LowBufferMs, MinBufferMs, MaxBufferMs);
        BalancedBufferMs = Math.Clamp(BalancedBufferMs, MinBufferMs, MaxBufferMs);
        StableBufferMs = Math.Clamp(StableBufferMs, MinBufferMs, MaxBufferMs);
    }

    public static Settings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            settings.ClampBuffers();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var temporary = FilePath + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Write($"Could not save settings: {ex.Message}");
        }
    }
}

public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(Settings.Folder, "log.txt");

    public static void Write(string line)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Settings.Folder);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Move(FilePath, FilePath + ".1", overwrite: true);
                File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {line}{Environment.NewLine}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Failing to write the log must not affect audio.
            }
        }
    }
}
