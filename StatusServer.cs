using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace PodBridge;

/// <summary>Local endpoint the browser extension reads to learn how much to delay video:
/// http://127.0.0.1:47389/status. Only reachable from this computer; without CORS headers
/// web pages cannot read the reply, and the Host check blocks DNS rebinding.</summary>
public sealed class StatusServer : IDisposable
{
    public const int DefaultPort = 47389;
    private const int MaxRequestBytes = 4096;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(2);

    private readonly TcpListener _listener;
    private readonly Func<object> _status;
    private readonly CancellationTokenSource _stop = new();

    public int Port { get; }

    public StatusServer(Func<object> status, int port = DefaultPort)
    {
        _status = status;
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client));
            }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException ex) { Log.Write($"Status endpoint: {ex.Message}"); }
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(RequestTimeout);
                var stream = client.GetStream();
                var buffer = new byte[MaxRequestBytes];
                int length = 0;
                while (length < buffer.Length)
                {
                    int read = await stream.ReadAsync(buffer.AsMemory(length), timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    length += read;
                    if (buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8) >= 0) break;
                }

                var lines = Encoding.ASCII.GetString(buffer, 0, length).Split("\r\n");
                string host = lines.Skip(1)
                    .FirstOrDefault(l => l.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))?[5..].Trim() ?? "";
                bool hostOk = host == $"127.0.0.1:{Port}" || host == $"localhost:{Port}";
                bool routeOk = lines[0].StartsWith("GET /status ", StringComparison.Ordinal);

                string body = hostOk && routeOk ? JsonSerializer.Serialize(_status()) : "{}";
                string status = !hostOk ? "403 Forbidden" : routeOk ? "200 OK" : "404 Not Found";
                var response = Encoding.UTF8.GetBytes(
                    $"HTTP/1.1 {status}\r\nContent-Type: application/json; charset=utf-8\r\n" +
                    $"Cache-Control: no-store\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}");
                await stream.WriteAsync(response, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
    }
}
