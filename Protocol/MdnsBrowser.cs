using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace PodBridge.Protocol;

public sealed record AirPlayDevice(string Id, string Name, string Model, string Address, int Port)
{
    public bool IsHomePod => Model.StartsWith("AudioAccessory", StringComparison.Ordinal);
}

/// <summary>Finds _airplay._tcp services with one-shot mDNS queries. The query is sent from a random source port,
/// so devices reply to that port directly and there is no need to share port 5353.</summary>
public static class MdnsBrowser
{
    private const string ServiceName = "_airplay._tcp.local";
    private static readonly IPEndPoint Multicast = new(IPAddress.Parse("224.0.0.251"), 5353);

    public static async Task<List<AirPlayDevice>> BrowseAsync(TimeSpan duration, CancellationToken ct = default)
    {
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up
                        && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && n.SupportsMulticast)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(a => a.Address)
            .ToList();

        var found = new Dictionary<string, AirPlayDevice>();
        var tasks = addresses.Select(a => BrowseInterfaceAsync(a, duration, found, ct));
        await Task.WhenAll(tasks).ConfigureAwait(false);
        lock (found) return [.. found.Values.OrderBy(d => d.Name, StringComparer.CurrentCulture)];
    }

    private static async Task BrowseInterfaceAsync(IPAddress local, TimeSpan duration,
                                                   Dictionary<string, AirPlayDevice> found, CancellationToken ct)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            socket.Bind(new IPEndPoint(local, 0));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 255);
        }
        catch (SocketException) { return; }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(duration);
        var query = BuildQuery();
        var buffer = new byte[9000];

        var sender = Task.Run(async () =>
        {
            try
            {
                while (!timeout.IsCancellationRequested)
                {
                    await socket.SendToAsync(query, Multicast, timeout.Token).ConfigureAwait(false);
                    await Task.Delay(800, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        });

        try
        {
            while (true)
            {
                int read = await socket.ReceiveAsync(buffer, SocketFlags.None, timeout.Token).ConfigureAwait(false);
                foreach (var device in Parse(buffer.AsSpan(0, read)))
                    lock (found) found[device.Id] = device;
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or SocketException) { }
        await sender.ConfigureAwait(false);
    }

    private static byte[] BuildQuery()
    {
        using var ms = new MemoryStream();
        ms.Write([0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0]);
        foreach (var label in ServiceName.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0);
        ms.Write([0, 12, 0, 1]);    // PTR, IN
        return ms.ToArray();
    }

    /// <summary>Collects every record in the reply (additional records included) and returns the complete devices.</summary>
    internal static List<AirPlayDevice> Parse(ReadOnlySpan<byte> message)
    {
        var result = new List<AirPlayDevice>();
        if (message.Length < 12) return result;
        int questions = BinaryPrimitives.ReadUInt16BigEndian(message[4..]);
        int records = BinaryPrimitives.ReadUInt16BigEndian(message[6..])
                    + BinaryPrimitives.ReadUInt16BigEndian(message[8..])
                    + BinaryPrimitives.ReadUInt16BigEndian(message[10..]);

        var ptrs = new List<string>();
        var srv = new Dictionary<string, (int Port, string Target)>(StringComparer.OrdinalIgnoreCase);
        var txt = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var hosts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            int pos = 12;
            for (int i = 0; i < questions; i++)
            {
                ReadName(message, ref pos);
                pos += 4;
            }
            for (int i = 0; i < records; i++)
            {
                string name = ReadName(message, ref pos);
                int type = BinaryPrimitives.ReadUInt16BigEndian(message[pos..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(message[(pos + 8)..]);
                pos += 10;
                int end = pos + length;
                if (end > message.Length) break;
                switch (type)
                {
                    case 12:
                        if (name.Equals(ServiceName, StringComparison.OrdinalIgnoreCase))
                        {
                            int p = pos;
                            ptrs.Add(ReadName(message, ref p));
                        }
                        break;
                    case 33:
                        {
                            int port = BinaryPrimitives.ReadUInt16BigEndian(message[(pos + 4)..]);
                            int p = pos + 6;
                            srv[name] = (port, ReadName(message, ref p));
                            break;
                        }
                    case 16:
                        txt[name] = ParseTxt(message[pos..end]);
                        break;
                    case 1 when length == 4:
                        hosts[name] = new IPAddress(message.Slice(pos, 4)).ToString();
                        break;
                }
                pos = end;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or FormatException)
        {
            // Malformed packet: keep what was parsed so far.
        }

        foreach (string instance in ptrs)
        {
            if (!srv.TryGetValue(instance, out var service)) continue;
            if (!hosts.TryGetValue(service.Target, out var address)) continue;
            var properties = txt.GetValueOrDefault(instance) ?? [];
            string id = properties.GetValueOrDefault("deviceid") ?? instance;
            string display = instance[..^(ServiceName.Length + 1)];
            result.Add(new AirPlayDevice(id, display, properties.GetValueOrDefault("model") ?? "", address, service.Port));
        }
        return result;
    }

    private static Dictionary<string, string> ParseTxt(ReadOnlySpan<byte> data)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int pos = 0;
        while (pos < data.Length)
        {
            int length = data[pos++];
            if (pos + length > data.Length) break;
            var entry = Encoding.UTF8.GetString(data.Slice(pos, length));
            int eq = entry.IndexOf('=');
            if (eq > 0) result[entry[..eq]] = entry[(eq + 1)..];
            pos += length;
        }
        return result;
    }

    private static string ReadName(ReadOnlySpan<byte> message, ref int pos)
    {
        var name = new StringBuilder();
        int cursor = pos;
        bool jumped = false;
        int guard = 0;
        while (true)
        {
            if (++guard > 128) throw new FormatException("DNS name too long");
            int length = message[cursor];
            if (length == 0)
            {
                if (!jumped) pos = cursor + 1;
                return name.ToString();
            }
            if ((length & 0xC0) == 0xC0)
            {
                int target = (length & 0x3F) << 8 | message[cursor + 1];
                if (!jumped) pos = cursor + 2;
                jumped = true;
                cursor = target;
                continue;
            }
            if (name.Length > 0) name.Append('.');
            name.Append(Encoding.UTF8.GetString(message.Slice(cursor + 1, length)));
            cursor += length + 1;
        }
    }
}
