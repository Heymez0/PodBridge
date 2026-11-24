using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace PodBridge.Protocol;

public enum SessionCloseReason
{
    /// <summary>Closed at our request.</summary>
    Local,
    /// <summary>The device closed the connection cleanly; usually another device took over the HomePod.</summary>
    DeviceClosed,
    /// <summary>Network error or the device stopped responding.</summary>
    Network,
}

public sealed class FirewallBlockedException(string message) : Exception(message);

public sealed class PairingRejectedException(string message) : Exception(message);

public sealed class RtspException(string step, int code) : Exception($"{step} rejected ({code})")
{
    public int Code { get; } = code;
}

public interface IPcmSource
{
    /// <summary>Fills the span with 44100 Hz stereo 16-bit samples. Writes silence when nothing is playing; never blocks.</summary>
    void Read(Span<short> interleaved);
}

/// <summary>Which part of the stereo signal a HomePod plays. Left and Right send that channel to both of the
/// HomePod's channels, so one HomePod per side makes a stereo pair.</summary>
public enum ChannelMode
{
    Stereo,
    Left,
    Right,
}

public sealed record SessionOptions(
    string ClientName,
    string DeviceId,
    int LatencyFrames,
    bool NetworkPriority = true,
    ChannelMode Channel = ChannelMode.Stereo);

/// <summary>One timeline for every HomePod in a group: the same start time and the same NTP start mean packet N
/// carries the same audio and is played at the same instant on each of them.</summary>
public sealed class StreamTimeline
{
    public long StartTicks { get; } = Stopwatch.GetTimestamp();
    public ulong StartNtp { get; }

    public StreamTimeline() => StartNtp = AirPlaySession.NtpAt(StartTicks);
}

/// <summary>Reads each packet's audio from the capture once and hands the same samples to every session that asks
/// for that packet number, so a group never splits the capture between its HomePods.</summary>
public sealed class PacketFanout(IPcmSource source)
{
    private const int Slots = 256;   // About two seconds of packets, far more than sessions drift apart.
    private readonly short[][] _cache = Enumerable.Range(0, Slots).Select(_ => new short[AirPlaySession.FramesPerPacket * 2]).ToArray();
    private readonly long[] _index = Enumerable.Repeat(-1L, Slots).ToArray();
    private readonly object _gate = new();
    private long _newest = -1;

    public void Read(long packet, Span<short> destination)
    {
        lock (_gate)
        {
            int slot = (int)(packet % Slots);
            if (_index[slot] != packet)
            {
                if (packet <= _newest)
                {
                    destination.Clear();   // Too old to still be cached: a session that stalled for seconds.
                    return;
                }
                source.Read(_cache[slot]);
                _index[slot] = packet;
                _newest = packet;
            }
            _cache[slot].CopyTo(destination);
        }
    }
}

/// <summary>A single AirPlay 2 realtime audio session. Pairing is transient (HAP transient),
/// so no PIN or long-term keys are needed. See NOTICE for where the protocol steps come from.</summary>
public sealed class AirPlaySession : IDisposable
{
    public const int SampleRate = 44100;
    public const int FramesPerPacket = 352;
    private const int Channels = 2;
    private const int BacklogSize = 1024;
    private const int RtpHeaderSize = 12;
    private const int TagSize = 16;
    private const int NonceSuffixSize = 8;
    private const int MaxBurstPackets = 16;
    private const int ResponseTimeoutMs = 8000;
    private const string UserAgent = "AirPlay/550.10";
    private const ulong NtpEpochOffset = 0x83AA7E80UL;

    private static readonly int PayloadSize = Alac.FrameSize(FramesPerPacket * Channels);
    private static readonly int PacketSize = RtpHeaderSize + PayloadSize + TagSize + NonceSuffixSize;

    private readonly IPAddress _host;
    private readonly int _port;
    private readonly SessionOptions _options;
    private readonly Action<string> _log;

    private readonly TcpClient _control = new(AddressFamily.InterNetwork);
    private NetworkStream? _controlStream;
    private SecureFrames? _controlFrames;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    // Replies are matched to requests by CSeq, so a request that timed out cannot shift every later reply by one.
    private readonly List<(int CSeq, TaskCompletionSource<RtspResponse> Waiter)> _pending = [];
    private readonly object _pendingLock = new();
    private readonly MemoryStream _rxPlain = new();
    private int _cseq;

    private TcpClient? _event;
    private SecureFrames? _eventFrames;

    private readonly Socket _timingSocket = NewUdp();
    private readonly Socket _controlSocket = NewUdp();
    private readonly Socket _audioSocket = NewUdp();
    private IPEndPoint? _remoteControl;
    private int _timingRequests;
    private readonly NetworkPriority.QosFlows? _qos;
    private int _nextSeq;
    private int _requestsLogged;
    private const int MaxRequestLogs = 300;
    private NetworkPriority.WifiStreamingMode? _wifi;

    private readonly uint _sessionId = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private readonly string _dacpId = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private readonly uint _activeRemote = (uint)RandomNumberGenerator.GetInt32(1, int.MaxValue);
    private readonly string _sessionUuid = Guid.NewGuid().ToString().ToUpperInvariant();
    private string _localIp = "";
    private byte[] _audioKey = [];

    // Time base: the wall clock is read once, everything after comes from the monotonic counter,
    // so a Windows clock sync in the middle of a session cannot shift the timeline.
    private static readonly long BaseTicks = Stopwatch.GetTimestamp();
    private static readonly ulong BaseNtp = ToNtp(DateTime.UtcNow);

    private readonly CancellationTokenSource _stop = new();
    private Thread? _audioThread;
    private readonly byte[] _backlog = new byte[BacklogSize * PacketSize];
    private readonly int[] _backlogSeq = new int[BacklogSize];
    private readonly object _backlogLock = new();
    private int _closed;

    public event Action<SessionCloseReason, string>? Closed;

    /// <summary>Time from packet arrival to sound, as reported by the HomePod in its SETUP reply.</summary>
    public long? ReceiverLatencyMs { get; private set; }

    public long PacketsSent => Volatile.Read(ref _packetsSent);
    private long _packetsSent;
    public long PacketsRetransmitted => Volatile.Read(ref _retransmitted);
    private long _retransmitted;

    public AirPlaySession(IPAddress host, int port, SessionOptions options, Action<string> log)
    {
        _host = host;
        _port = port;
        _options = options;
        _log = log;
        _qos = options.NetworkPriority ? new NetworkPriority.QosFlows(log) : null;
        Array.Fill(_backlogSeq, -1);
    }

    private static Socket NewUdp()
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        socket.Bind(new IPEndPoint(IPAddress.Any, 0));
        // Room for bursts after a scheduling hiccup and for answering a large retransmit request at once.
        socket.SendBufferSize = 512 * 1024;
        socket.ReceiveBufferSize = 256 * 1024;
        // Windows reports an ICMP "port unreachable" as an error on the next receive; turn that off.
        const int SioUdpConnReset = unchecked((int)0x9800000C);
        socket.IOControl(SioUdpConnReset, [0], null);
        return socket;
    }

    private static int PortOf(Socket socket) => ((IPEndPoint)socket.LocalEndPoint!).Port;

    // ---------------------------------------------------------------- connection

    public async Task ConnectAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(6));
        await _control.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
        _control.NoDelay = true;
        _controlStream = _control.GetStream();
        _localIp = ((IPEndPoint)_control.Client.LocalEndPoint!).Address.ToString();
        _ = Task.Run(ReadControlLoop);
        StartBackground(TimingLoop, "AirPlay timing");
        StartBackground(ControlLoop, "AirPlay control");

        await PairAsync().ConfigureAwait(false);

        var info = await RtspAsync("GET", "/info").ConfigureAwait(false);
        Require("GET /info", info);

        var setup = await SetupSessionAsync().ConfigureAwait(false);
        await OpenEventChannelAsync(setup).ConfigureAwait(false);

        var record = await RtspAsync("RECORD", Uri).ConfigureAwait(false);
        _log($"RECORD reply: {record.Code}");

        await SetupStreamAsync().ConfigureAwait(false);
    }

    private string Uri => $"rtsp://{_localIp}/{_sessionId}";

    private async Task PairAsync()
    {
        var m2 = await PairPostAsync(Tlv8.Encode(
            (Tlv8.Method, [0x00]), (Tlv8.State, [0x01]), (Tlv8.Flags, [0x10]))).ConfigureAwait(false);
        if (m2.TryGetValue(Tlv8.Error, out var e1)) throw new PairingRejectedException($"M2 error {e1[0]}");

        var srp = new SrpClient();
        if (!srp.Process("3939", m2[Tlv8.Salt], m2[Tlv8.PublicKey]))
            throw new PairingRejectedException("Invalid SRP parameters");

        var m4 = await PairPostAsync(Tlv8.Encode(
            (Tlv8.State, [0x03]), (Tlv8.PublicKey, srp.PublicA), (Tlv8.Proof, srp.ProofM1))).ConfigureAwait(false);
        if (m4.TryGetValue(Tlv8.Error, out var e2)) throw new PairingRejectedException($"M4 error {e2[0]}");
        if (!m4.TryGetValue(Tlv8.Proof, out var proof) || !srp.VerifyServerProof(proof))
            throw new PairingRejectedException("Could not verify the device's pairing proof");

        var secret = srp.SessionKey;
        _audioKey = secret[..32];
        _controlFrames = new SecureFrames(
            Derive(secret, "Control-Salt", "Control-Write-Encryption-Key"),
            Derive(secret, "Control-Salt", "Control-Read-Encryption-Key"));
        // The event channel runs in the reverse direction, so the read and write keys are swapped.
        _eventFrames = new SecureFrames(
            Derive(secret, "Events-Salt", "Events-Read-Encryption-Key"),
            Derive(secret, "Events-Salt", "Events-Write-Encryption-Key"));
        _log("Pairing complete, control channel encrypted");
    }

    private static byte[] Derive(byte[] secret, string salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA512, secret, 32,
                       Encoding.ASCII.GetBytes(salt), Encoding.ASCII.GetBytes(info));

    private async Task<Dictionary<byte, byte[]>> PairPostAsync(byte[] body)
    {
        var response = await SendAsync("POST", "/pair-setup", "HTTP/1.1", "application/octet-stream", body,
                                       ("Connection", "keep-alive"), ("X-Apple-HKP", "4")).ConfigureAwait(false);
        if (response.Code is 403 or 470)
            throw new PairingRejectedException(
                Ui.L.T($"The HomePod refused pairing ({response.Code}). In the Home app, check " +
                       "Home Settings > Speakers & TV > Allow Speaker & TV Access.",
                       $"HomePod eşleştirmeyi reddetti ({response.Code}). Ev uygulamasında " +
                       "Ev Ayarları > Hoparlörler ve TV > Erişime İzin Ver seçeneğini kontrol et."));
        Require("pair-setup", response);
        return Tlv8.Decode(response.Body);
    }

    private async Task<Dictionary<string, object?>> SetupSessionAsync()
    {
        var body = BPlist.Encode(new Dictionary<string, object>
        {
            ["deviceID"] = _options.DeviceId,
            ["sessionUUID"] = _sessionUuid,
            ["timingPort"] = PortOf(_timingSocket),
            ["timingProtocol"] = "NTP",
            ["isMultiSelectAirPlay"] = true,
            ["groupContainsGroupLeader"] = false,
            ["macAddress"] = _options.DeviceId,
            ["model"] = "iPhone14,3",
            ["name"] = _options.ClientName,
            ["osBuildVersion"] = "20F66",
            ["osName"] = "iPhone OS",
            ["osVersion"] = "16.5",
            ["senderSupportsRelay"] = false,
            ["sourceVersion"] = "690.7.1",
            ["statsCollectionEnabled"] = false,
        });

        RtspResponse response;
        try
        {
            response = await SendAsync("SETUP", Uri, "RTSP/1.0", "application/x-apple-binary-plist", body,
                                       ("X-Apple-StreamID", "1")).ConfigureAwait(false);
        }
        catch (TimeoutException) when (Volatile.Read(ref _timingRequests) == 0)
        {
            // The HomePod syncs its clock with us before replying. If that request never arrived, the firewall is to blame.
            throw new FirewallBlockedException("The HomePod's timing packets are not reaching this computer.");
        }
        Require("SETUP (session)", response);
        var plist = BPlist.Decode(response.Body) as Dictionary<string, object?> ?? [];
        _log($"Session SETUP complete: {string.Join(", ", plist.Keys)}");
        return plist;
    }

    private async Task OpenEventChannelAsync(Dictionary<string, object?> setup)
    {
        if (setup.GetValueOrDefault("eventPort") is not long eventPort || eventPort == 0) return;
        _event = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
        await _event.ConnectAsync(_host, (int)eventPort).ConfigureAwait(false);
        _ = Task.Run(ReadEventLoop);
    }

    private async Task SetupStreamAsync()
    {
        int latency = _options.LatencyFrames;
        var body = BPlist.Encode(new Dictionary<string, object>
        {
            ["streams"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["audioFormat"] = 0x40000,
                    ["audioMode"] = "default",
                    ["controlPort"] = PortOf(_controlSocket),
                    ["ct"] = 2,
                    ["isMedia"] = true,
                    ["latencyMax"] = Math.Max(88200, latency),
                    ["latencyMin"] = Math.Min(11025, latency),
                    ["shk"] = _audioKey,
                    ["spf"] = FramesPerPacket,
                    ["sr"] = SampleRate,
                    ["type"] = 0x60,
                    ["supportsDynamicStreamID"] = false,
                    ["streamConnectionID"] = (long)_sessionId,
                },
            },
        });
        RtspResponse response;
        try
        {
            response = await SendAsync("SETUP", Uri, "RTSP/1.0", "application/x-apple-binary-plist", body,
                                       ("X-Apple-StreamID", "1")).ConfigureAwait(false);
        }
        catch (TimeoutException) when (Volatile.Read(ref _timingRequests) == 0)
        {
            throw new FirewallBlockedException("The HomePod's timing packets are not reaching this computer.");
        }
        Require("SETUP (stream)", response);

        var plist = BPlist.Decode(response.Body) as Dictionary<string, object?>;
        var stream = (plist?.GetValueOrDefault("streams") as List<object?>)?.FirstOrDefault() as Dictionary<string, object?>;
        if (stream?.GetValueOrDefault("dataPort") is not long dataPort || dataPort == 0)
            throw new IOException("The HomePod did not report a data port");
        long controlPort = stream.GetValueOrDefault("controlPort") as long? ?? dataPort;

        var data = new IPEndPoint(_host, (int)dataPort);
        _audioSocket.Connect(data);
        _remoteControl = new IPEndPoint(_host, (int)controlPort);
        _qos?.Add(_audioSocket, data, NetworkPriority.TrafficType.AudioVideo);
        _qos?.Add(_controlSocket, _remoteControl, NetworkPriority.TrafficType.AudioVideo);
        if (_qos == null) _log("Network priority off: packets sent unmarked, Wi-Fi left as it is");
        ReceiverLatencyMs = stream.GetValueOrDefault("arrivalToRenderLatencyMs") as long?;
        _log($"Stream SETUP complete: data={dataPort} control={controlPort} device latency={ReceiverLatencyMs} ms");
    }

    private static void Require(string step, RtspResponse response)
    {
        if (response.Code is < 200 or >= 300) throw new RtspException(step, response.Code);
    }

    // ---------------------------------------------------------------- RTSP

    private sealed record RtspResponse(int Code, Dictionary<string, string> Headers, byte[] Body);

    private Task<RtspResponse> RtspAsync(string method, string uri) =>
        SendAsync(method, uri, "RTSP/1.0", null, null);

    private async Task<RtspResponse> SendAsync(string method, string uri, string protocol, string? contentType,
                                               byte[]? body, params (string Name, string Value)[] extra)
    {
        var head = new StringBuilder(256);
        head.Append(method).Append(' ').Append(uri).Append(' ').Append(protocol).Append("\r\n");
        int cseq = Interlocked.Increment(ref _cseq);
        head.Append("CSeq: ").Append(cseq).Append("\r\n");
        head.Append("User-Agent: ").Append(UserAgent).Append("\r\n");
        head.Append("DACP-ID: ").Append(_dacpId).Append("\r\n");
        head.Append("Active-Remote: ").Append(_activeRemote).Append("\r\n");
        head.Append("Client-Instance: ").Append(_dacpId).Append("\r\n");
        head.Append("X-Apple-Client-Name: ").Append(_options.ClientName).Append("\r\n");
        foreach (var (name, value) in extra) head.Append(name).Append(": ").Append(value).Append("\r\n");
        if (contentType != null) head.Append("Content-Type: ").Append(contentType).Append("\r\n");
        if (body != null) head.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        head.Append("\r\n");
        byte[] message = [.. Encoding.UTF8.GetBytes(head.ToString()), .. body ?? []];

        var pending = new TaskCompletionSource<RtspResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _writeLock.WaitAsync().ConfigureAwait(false);
        try
        {
            lock (_pendingLock) _pending.Add((cseq, pending));
            var wire = _controlFrames?.Seal(message) ?? message;
            await _controlStream!.WriteAsync(wire).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }

        var finished = await Task.WhenAny(pending.Task, Task.Delay(ResponseTimeoutMs)).ConfigureAwait(false);
        if (finished != pending.Task)
        {
            lock (_pendingLock) _pending.RemoveAll(p => p.Waiter == pending);
            throw new TimeoutException($"{method} {uri} got no reply");
        }
        return await pending.Task.ConfigureAwait(false);
    }

    private async Task ReadControlLoop()
    {
        var buffer = new byte[16384];
        SessionCloseReason reason = SessionCloseReason.Network;
        string detail = "";
        try
        {
            while (true)
            {
                int read = await _controlStream!.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read == 0) { reason = SessionCloseReason.DeviceClosed; break; }
                var plain = _controlFrames != null ? _controlFrames.Open(buffer.AsSpan(0, read)) : buffer.AsSpan(0, read).ToArray();
                _rxPlain.Write(plain);
                DispatchResponses();
            }
        }
        catch (OperationCanceledException) { reason = SessionCloseReason.Local; }
        catch (Exception ex) { detail = ex.Message; }
        lock (_pendingLock)
        {
            foreach (var (_, waiter) in _pending)
                waiter.TrySetException(new IOException("Control connection closed"));
            _pending.Clear();
        }
        Shutdown(reason, detail);
    }

    private void DispatchResponses()
    {
        while (true)
        {
            var data = _rxPlain.GetBuffer().AsSpan(0, (int)_rxPlain.Length);
            int headEnd = data.IndexOf("\r\n\r\n"u8);
            if (headEnd < 0) return;

            var lines = Encoding.UTF8.GetString(data[..headEnd]).Split("\r\n");
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
            int length = int.TryParse(headers.GetValueOrDefault("Content-Length"), out int n) && n > 0 ? n : 0;
            if (data.Length < headEnd + 4 + length) return;

            var body = data.Slice(headEnd + 4, length).ToArray();
            var rest = data[(headEnd + 4 + length)..].ToArray();
            _rxPlain.SetLength(0);
            _rxPlain.Write(rest);

            var status = lines[0].Split(' ');
            if (status.Length >= 2 && int.TryParse(status[1], out int code))
            {
                TaskCompletionSource<RtspResponse>? waiter = null;
                lock (_pendingLock)
                {
                    // Fall back to the oldest request if the reply carries no CSeq we know.
                    int index = int.TryParse(headers.GetValueOrDefault("CSeq"), out int replyCSeq)
                        ? _pending.FindIndex(p => p.CSeq == replyCSeq) : -1;
                    if (index < 0 && _pending.Count > 0) index = 0;
                    if (index >= 0)
                    {
                        waiter = _pending[index].Waiter;
                        _pending.RemoveAt(index);
                    }
                }
                waiter?.TrySetResult(new RtspResponse(code, headers, body));
            }
        }
    }

    // The HomePod sends POST /command on the event channel and closes the session within 30 seconds if unanswered.
    // Keep the reply as bare as possible: extra headers silence the audio.
    private async Task ReadEventLoop()
    {
        var buffer = new byte[8192];
        var plain = new MemoryStream();
        try
        {
            var stream = _event!.GetStream();
            while (true)
            {
                int read = await stream.ReadAsync(buffer, _stop.Token).ConfigureAwait(false);
                if (read == 0) return;
                plain.Write(_eventFrames!.Open(buffer.AsSpan(0, read)));
                while (TryTakeRequest(plain, out var cseq, out var firstLine))
                {
                    _log($"Event: {firstLine}");
                    var reply = "RTSP/1.0 200 OK\r\nServer: AirTunes/550.10\r\n" +
                                (cseq != null ? $"CSeq: {cseq}\r\n" : "") + "\r\n";
                    await stream.WriteAsync(_eventFrames.Seal(Encoding.ASCII.GetBytes(reply))).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log($"Event channel: {ex.Message}"); }
    }

    private static bool TryTakeRequest(MemoryStream plain, out string? cseq, out string firstLine)
    {
        cseq = null;
        firstLine = "";
        var data = plain.GetBuffer().AsSpan(0, (int)plain.Length);
        int headEnd = data.IndexOf("\r\n\r\n"u8);
        if (headEnd < 0) return false;
        var lines = Encoding.UTF8.GetString(data[..headEnd]).Split("\r\n");
        firstLine = lines[0];
        int length = 0;
        foreach (var line in lines.Skip(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line[..colon].Trim();
            var value = line[(colon + 1)..].Trim();
            if (key.Equals("CSeq", StringComparison.OrdinalIgnoreCase)) cseq = value;
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) int.TryParse(value, out length);
        }
        if (data.Length < headEnd + 4 + length) return false;
        var rest = data[(headEnd + 4 + length)..].ToArray();
        plain.SetLength(0);
        plain.Write(rest);
        return true;
    }

    // ---------------------------------------------------------------- UDP

    private void StartBackground(ThreadStart body, string name) =>
        new Thread(body) { IsBackground = true, Name = name, Priority = ThreadPriority.AboveNormal }.Start();

    private void TimingLoop()
    {
        var buffer = new byte[128];
        var reply = new byte[32];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                int read = _timingSocket.ReceiveFrom(buffer, ref from);
                if (read < 32) continue;
                ulong now = NtpNow();
                reply[0] = buffer[0];
                reply[1] = 0xD3;
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(2), 0x0007);
                reply.AsSpan(4, 4).Clear();
                buffer.AsSpan(24, 8).CopyTo(reply.AsSpan(8));
                BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(16), now);
                BinaryPrimitives.WriteUInt64BigEndian(reply.AsSpan(24), now);
                _timingSocket.SendTo(reply, from);
                if (Interlocked.Increment(ref _timingRequests) == 1)
                {
                    _log("HomePod clock sync started");
                    // Clock sync replies are tiny and their delay feeds straight into the HomePod's timeline estimate.
                    _qos?.Add(_timingSocket, (IPEndPoint)from, NetworkPriority.TrafficType.Voice);
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    private void ControlLoop()
    {
        var buffer = new byte[128];
        var resend = new byte[PacketSize + 4];
        EndPoint from = new IPEndPoint(IPAddress.Any, 0);
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                int read = _controlSocket.ReceiveFrom(buffer, ref from);
                if (read < 8 || (buffer[1] & 0x7F) != 0x55) continue;
                ushort lost = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4));
                ushort count = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6));
                int behind = (ushort)(Volatile.Read(ref _nextSeq) - lost);
                int resent = 0;
                for (int i = 0; i < Math.Min((int)count, BacklogSize); i++)
                {
                    ushort seq = (ushort)(lost + i);
                    int slot = seq & (BacklogSize - 1);
                    lock (_backlogLock)
                    {
                        if (_backlogSeq[slot] != seq) continue;
                        _backlog.AsSpan(slot * PacketSize, PacketSize).CopyTo(resend.AsSpan(4));
                    }
                    resend[0] = 0x80;
                    resend[1] = 0xD6;
                    BinaryPrimitives.WriteUInt16BigEndian(resend.AsSpan(2), seq);
                    _controlSocket.SendTo(resend, from);
                    Interlocked.Increment(ref _retransmitted);
                    resent++;
                }
                // How far back the request reaches tells whether the resent audio can still be played in time.
                if (Interlocked.Increment(ref _requestsLogged) <= MaxRequestLogs)
                    _log($"Retransmit request: {count} packets, oldest sent {behind * FramesPerPacket * 1000 / SampleRate} ms ago, {resent} resent");
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { return; }
        }
    }

    // ---------------------------------------------------------------- audio

    public void StartStreaming(IPcmSource source) => StartStreaming(new PacketFanout(source), new StreamTimeline());

    /// <summary>Starts sending on a timeline shared with the other HomePods of a group.</summary>
    public void StartStreaming(PacketFanout source, StreamTimeline timeline)
    {
        if (_options.NetworkPriority) _wifi = new NetworkPriority.WifiStreamingMode(_log);
        _audioThread = new Thread(() => AudioLoop(source, timeline))
        {
            IsBackground = true,
            Name = "AirPlay audio",
            Priority = ThreadPriority.Highest,
        };
        _audioThread.Start();
        _ = Task.Run(FeedbackLoop);
    }

    private void AudioLoop(PacketFanout source, StreamTimeline timeline)
    {
        using var mmcss = NativeTiming.EnterProAudio();
        using var timer = new NativeTiming.PreciseTimer();

        Span<short> pcm = new short[FramesPerPacket * Channels];
        var packet = new byte[PacketSize];
        Span<byte> nonce = stackalloc byte[12];
        using var cipher = new ChaCha20Poly1305(_audioKey);

        ushort seq = (ushort)RandomNumberGenerator.GetInt32(ushort.MaxValue);
        uint ssrc = _sessionId;
        ulong nonceCounter = 0;
        long framesSent = 0;
        bool first = true;
        long start = timeline.StartTicks;
        ulong startNtp = timeline.StartNtp;
        long nextSync = start;
        var channel = _options.Channel;
        uint latency = (uint)_options.LatencyFrames;
        var syncPacket = new byte[20];

        SendSync(syncPacket, framesSent, startNtp, latency, true);
        nextSync += Stopwatch.Frequency;

        while (!_stop.IsCancellationRequested)
        {
            long now = Stopwatch.GetTimestamp();
            long target = (long)((now - start) * (double)SampleRate / Stopwatch.Frequency);

            int burst = 0;
            while (framesSent + FramesPerPacket <= target && burst++ < MaxBurstPackets)
            {
                source.Read(framesSent / FramesPerPacket, pcm);
                if (channel != ChannelMode.Stereo) PickChannel(pcm, channel);
                packet[0] = 0x80;
                packet[1] = first ? (byte)0xE0 : (byte)0x60;
                BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), seq);
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), unchecked((uint)framesSent + latency));
                BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(8), ssrc);

                var plain = packet.AsSpan(RtpHeaderSize, PayloadSize);
                Alac.Encode(pcm, plain);

                nonce[..4].Clear();
                BinaryPrimitives.WriteUInt64LittleEndian(nonce[4..], nonceCounter);
                cipher.Encrypt(nonce, plain, plain, packet.AsSpan(RtpHeaderSize + PayloadSize, TagSize), packet.AsSpan(4, 8));
                nonce[4..].CopyTo(packet.AsSpan(RtpHeaderSize + PayloadSize + TagSize));
                nonceCounter++;

                int slot = seq & (BacklogSize - 1);
                lock (_backlogLock)
                {
                    packet.CopyTo(_backlog.AsSpan(slot * PacketSize));
                    _backlogSeq[slot] = seq;
                }

                try { _audioSocket.Send(packet); }
                catch (SocketException) { }   // Transient network error; the feedback loop catches persistent ones.
                catch (ObjectDisposedException) { return; }   // The session shut down while this thread was still sending.

                first = false;
                seq++;
                Volatile.Write(ref _nextSeq, seq);
                framesSent += FramesPerPacket;
                Interlocked.Increment(ref _packetsSent);
            }

            // If the computer stalled for a long time (sleep, for example), skip ahead instead of trying to catch up.
            if (target - framesSent > SampleRate)
                framesSent = target / FramesPerPacket * FramesPerPacket;

            if (now >= nextSync)
            {
                SendSync(syncPacket, framesSent, startNtp, latency, false);
                nextSync += Stopwatch.Frequency;
            }
            timer.Wait(1);
        }
    }

    private void SendSync(byte[] packet, long framesSent, ulong startNtp, uint latency, bool first)
    {
        if (_remoteControl == null) return;
        ulong seconds = (ulong)(framesSent / SampleRate);
        ulong remainder = (ulong)(framesSent % SampleRate);
        ulong ntp = startNtp + (seconds << 32) + (remainder << 32) / SampleRate;

        packet[0] = first ? (byte)0x90 : (byte)0x80;
        packet[1] = 0xD4;
        BinaryPrimitives.WriteUInt16BigEndian(packet.AsSpan(2), 0x0007);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(4), unchecked((uint)framesSent));
        BinaryPrimitives.WriteUInt64BigEndian(packet.AsSpan(8), ntp);
        BinaryPrimitives.WriteUInt32BigEndian(packet.AsSpan(16), unchecked((uint)framesSent + latency));
        try { _controlSocket.SendTo(packet, _remoteControl); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private async Task FeedbackLoop()
    {
        int failures = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(2000, _stop.Token).ConfigureAwait(false);
                await SendAsync("POST", "/feedback", "RTSP/1.0", null, null, ("Content-Length", "0")).ConfigureAwait(false);
                failures = 0;
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                if (++failures >= 2)
                {
                    Shutdown(SessionCloseReason.Network, ex.Message);
                    return;
                }
            }
        }
    }

    /// <summary>Maps 0..100 percent onto AirPlay's -30..0 dB range; 0 is mute.</summary>
    public async Task SetVolumeAsync(double percent)
    {
        double db = percent < 0.5 ? -144.0 : (percent * 3.0 - 300.0) / 10.0;
        var body = Encoding.ASCII.GetBytes("volume: " + db.ToString("F6", CultureInfo.InvariantCulture));
        var response = await SendAsync("SET_PARAMETER", Uri, "RTSP/1.0", "text/parameters", body).ConfigureAwait(false);
        if (response.Code != 200) _log($"Volume rejected: {response.Code}");
    }

    public async Task<double?> GetVolumePercentAsync()
    {
        var body = Encoding.ASCII.GetBytes("volume\r\n");
        var response = await SendAsync("GET_PARAMETER", Uri, "RTSP/1.0", "text/parameters", body).ConfigureAwait(false);
        var text = Encoding.ASCII.GetString(response.Body);
        int colon = text.IndexOf(':');
        if (response.Code != 200 || colon < 0
            || !double.TryParse(text[(colon + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double db))
            return null;
        return db <= -30 ? 0 : Math.Clamp((db + 30) * 100 / 30, 0, 100);
    }

    // ---------------------------------------------------------------- time

    private static ulong ToNtp(DateTime utc)
    {
        long ticks = utc.Ticks - DateTime.UnixEpoch.Ticks;
        ulong seconds = (ulong)(ticks / TimeSpan.TicksPerSecond) + NtpEpochOffset;
        ulong fraction = ((ulong)(ticks % TimeSpan.TicksPerSecond) << 32) / TimeSpan.TicksPerSecond;
        return seconds << 32 | fraction;
    }

    /// <summary>Copies one channel into both, for a HomePod that plays one side of a stereo pair.</summary>
    internal static void PickChannel(Span<short> interleaved, ChannelMode channel)
    {
        int keep = channel == ChannelMode.Left ? 0 : 1;
        for (int i = 0; i < interleaved.Length; i += 2)
        {
            short sample = interleaved[i + keep];
            interleaved[i] = sample;
            interleaved[i + 1] = sample;
        }
    }

    private static ulong NtpNow() => NtpAt(Stopwatch.GetTimestamp());

    /// <summary>NTP time of a Stopwatch timestamp. The timestamp may predate the clock base (a timeline created just
    /// before this class is first touched), so the offset is computed in either direction.</summary>
    internal static ulong NtpAt(long ticks)
    {
        long elapsed = ticks - BaseTicks;
        ulong magnitude = (ulong)Math.Abs(elapsed);
        ulong seconds = magnitude / (ulong)Stopwatch.Frequency;
        ulong remainder = magnitude % (ulong)Stopwatch.Frequency;
        ulong offset = (seconds << 32) + (remainder << 32) / (ulong)Stopwatch.Frequency;
        return elapsed >= 0 ? BaseNtp + offset : BaseNtp - offset;
    }

    // ---------------------------------------------------------------- shutdown

    private void Shutdown(SessionCloseReason reason, string detail)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        _stop.Cancel();
        _audioThread?.Join(500);
        _wifi?.Dispose();
        _qos?.Dispose();
        _timingSocket.Dispose();
        _controlSocket.Dispose();
        _audioSocket.Dispose();
        _event?.Dispose();
        _control.Dispose();
        _controlFrames?.Dispose();
        _eventFrames?.Dispose();
        _log($"Session closed: {reason} {detail}");
        Closed?.Invoke(reason, detail);
    }

    public async Task StopAsync()
    {
        if (Volatile.Read(ref _closed) != 0) return;
        try
        {
            using var timeout = new CancellationTokenSource(800);
            await SendAsync("TEARDOWN", Uri, "RTSP/1.0", null, null).WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) { _log($"TEARDOWN: {ex.Message}"); }
        Shutdown(SessionCloseReason.Local, "");
    }

    public void Dispose() => Shutdown(SessionCloseReason.Local, "");
}

internal static partial class NativeTiming
{
    [LibraryImport("avrt.dll", EntryPoint = "AvSetMmThreadCharacteristicsW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr AvSetMmThreadCharacteristics(string task, out uint index);

    [LibraryImport("avrt.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AvRevertMmThreadCharacteristics(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateWaitableTimerExW", SetLastError = true)]
    private static partial IntPtr CreateWaitableTimerEx(IntPtr attributes, IntPtr name, uint flags, uint access);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWaitableTimer(IntPtr timer, in long dueTime, int period, IntPtr completion, IntPtr arg,
                                                 [MarshalAs(UnmanagedType.Bool)] bool resume);

    [LibraryImport("kernel32.dll")]
    private static partial uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    private const uint CreateHighResolution = 0x2;
    private const uint TimerAllAccess = 0x1F0003;

    public static IDisposable EnterProAudio() => new ProAudioScope(AvSetMmThreadCharacteristics("Pro Audio", out _));

    private sealed class ProAudioScope(IntPtr handle) : IDisposable
    {
        public void Dispose()
        {
            if (handle != IntPtr.Zero) AvRevertMmThreadCharacteristics(handle);
        }
    }

    public sealed class PreciseTimer : IDisposable
    {
        private readonly IntPtr _handle = CreateWaitableTimerEx(IntPtr.Zero, IntPtr.Zero, CreateHighResolution, TimerAllAccess);

        public void Wait(int milliseconds)
        {
            if (_handle == IntPtr.Zero) { Thread.Sleep(milliseconds); return; }
            long due = -10_000L * milliseconds;
            if (SetWaitableTimer(_handle, in due, 0, IntPtr.Zero, IntPtr.Zero, false))
                WaitForSingleObject(_handle, 1000);
        }

        public void Dispose()
        {
            if (_handle != IntPtr.Zero) CloseHandle(_handle);
        }
    }
}
