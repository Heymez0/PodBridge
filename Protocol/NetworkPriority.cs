using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace PodBridge.Protocol;

/// <summary>Asks Windows to prioritize the stream's packets. Both measures are best effort: if Windows refuses,
/// streaming works as before. Nothing here outlives the session.</summary>
internal static partial class NetworkPriority
{
    // ---------------------------------------------------------------- QoS marking (qWAVE)

    public enum TrafficType
    {
        AudioVideo = 3,
        Voice = 4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct QosVersion
    {
        public ushort Major;
        public ushort Minor;
    }

    [LibraryImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QOSCreateHandle(in QosVersion version, out IntPtr handle);

    [LibraryImport("qwave.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QOSAddSocketToFlow(IntPtr handle, IntPtr socket, byte[] destination, int trafficType,
                                                   uint flags, ref uint flowId);

    [LibraryImport("qwave.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QOSCloseHandle(IntPtr handle);

    private const uint NonAdaptiveFlow = 0x2;

    /// <summary>Tags a socket's packets to one destination with a traffic class. On Wi-Fi this maps to the WMM video or
    /// voice queue, which the access point serves before ordinary traffic, so audio waits less and is dropped less.</summary>
    public sealed class QosFlows : IDisposable
    {
        private readonly IntPtr _handle;
        private readonly Action<string> _log;
        private readonly object _gate = new();
        private bool _loggedFailure;

        public QosFlows(Action<string> log)
        {
            _log = log;
            try
            {
                if (!QOSCreateHandle(new QosVersion { Major = 1, Minor = 0 }, out _handle))
                {
                    _handle = IntPtr.Zero;
                    _log($"QoS unavailable (error {Marshal.GetLastPInvokeError()})");
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _handle = IntPtr.Zero;
            }
        }

        public void Add(Socket socket, IPEndPoint destination, TrafficType type)
        {
            if (_handle == IntPtr.Zero) return;
            lock (_gate)
            {
                uint flow = 0;
                try
                {
                    if (QOSAddSocketToFlow(_handle, socket.Handle, SockAddr(destination), (int)type, NonAdaptiveFlow, ref flow))
                        return;
                    if (_loggedFailure) return;
                    _loggedFailure = true;
                    _log($"QoS marking refused (error {Marshal.GetLastPInvokeError()}); packets are sent unmarked");
                }
                catch (ObjectDisposedException) { }
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                // Closing the handle removes every flow added through it.
                if (_handle != IntPtr.Zero) QOSCloseHandle(_handle);
            }
        }

        private static byte[] SockAddr(IPEndPoint endpoint)
        {
            var address = new byte[16];
            BinaryPrimitives.WriteUInt16LittleEndian(address, (ushort)AddressFamily.InterNetwork);
            BinaryPrimitives.WriteUInt16BigEndian(address.AsSpan(2), (ushort)endpoint.Port);
            endpoint.Address.MapToIPv4().TryWriteBytes(address.AsSpan(4, 4), out _);
            return address;
        }
    }

    // ---------------------------------------------------------------- Wi-Fi streaming mode

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr handle);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanCloseHandle(IntPtr handle, IntPtr reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr interfaceList);

    [LibraryImport("wlanapi.dll")]
    private static partial uint WlanSetInterface(IntPtr handle, in Guid interfaceGuid, int opCode, uint dataSize,
                                                 in int data, IntPtr reserved);

    [LibraryImport("wlanapi.dll")]
    private static partial void WlanFreeMemory(IntPtr memory);

    private const int OpBackgroundScanEnabled = 2;
    private const int OpMediaStreamingMode = 3;
    private const int StateConnected = 1;
    private const int InterfaceInfoSize = 16 + 512 + 4;   // GUID, WCHAR[256] description, state

    /// <summary>While open, turns off Wi-Fi background scans and turns on the driver's media streaming mode for this
    /// process. Background scans take the radio off channel every minute or so, long enough to lose a burst of audio
    /// packets. Windows reverts both when the handle is closed or the process exits.</summary>
    public sealed class WifiStreamingMode : IDisposable
    {
        private IntPtr _handle;

        public WifiStreamingMode(Action<string> log)
        {
            try
            {
                if (WlanOpenHandle(2, IntPtr.Zero, out _, out _handle) != 0) { _handle = IntPtr.Zero; return; }
                if (WlanEnumInterfaces(_handle, IntPtr.Zero, out var list) != 0) return;
                try
                {
                    int count = Marshal.ReadInt32(list);
                    int tuned = 0;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr info = list + 8 + i * InterfaceInfoSize;
                        if (Marshal.ReadInt32(info + 16 + 512) != StateConnected) continue;
                        var guid = Marshal.PtrToStructure<Guid>(info);
                        bool scanOff = WlanSetInterface(_handle, guid, OpBackgroundScanEnabled, 4, 0, IntPtr.Zero) == 0;
                        bool streaming = WlanSetInterface(_handle, guid, OpMediaStreamingMode, 4, 1, IntPtr.Zero) == 0;
                        if (scanOff || streaming) tuned++;
                    }
                    if (tuned > 0) log("Wi-Fi: background scans paused and streaming mode on while streaming");
                }
                finally
                {
                    WlanFreeMemory(list);
                }
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                _handle = IntPtr.Zero;   // No Wi-Fi service on this PC.
            }
        }

        public void Dispose()
        {
            var handle = Interlocked.Exchange(ref _handle, IntPtr.Zero);
            if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero);
        }
    }
}
