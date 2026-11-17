using System.Net;
using System.Net.Sockets;

namespace FrameLine.Core.Mcp;

/// Loopback HTTP listener on a raw TcpListener (HttpListener would need a URL ACL / admin rights
/// on Windows). Binds 127.0.0.1 only, on a free port picked by the system (or a requested one).
/// One request per connection (Connection: close), 30 s budget per connection, non-loopback
/// peers dropped.
public sealed class McpHttpListener : IDisposable
{
    public static readonly TimeSpan ConnectionTimeout = TimeSpan.FromSeconds(30);
    public const int MaxConcurrentConnections = 32;
    private const int ReadChunk = 64 * 1024;

    private readonly McpHttpHandler _handler;
    private readonly object _lock = new();
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private SemaphoreSlim? _slots;

    /// Raised (on a pool thread) when a listener that was running dies on its own.
    public event Action<string>? Stopped;

    public McpHttpListener(McpHttpHandler handler) => _handler = handler;

    public int Port { get; private set; }
    public bool IsRunning { get { lock (_lock) return _listener is not null; } }

    /// Binds and starts accepting; returns the bound port. port 0 lets the system choose.
    public int Start(int port = 0)
    {
        lock (_lock)
        {
            if (_listener is not null) return Port;
            var listener = new TcpListener(IPAddress.Loopback, port);
            // Windows: forbid another process from binding the same port with SO_REUSEADDR and
            // stealing connections (port hijacking). Only supported on Windows.
            if (OperatingSystem.IsWindows()) listener.ExclusiveAddressUse = true;
            try { listener.Start(backlog: 64); }
            catch (SocketException e) { throw new IOException($"Cannot listen on 127.0.0.1:{port}: {e.Message}", e); }
            _listener = listener;
            _cts = new CancellationTokenSource();
            _slots = new SemaphoreSlim(MaxConcurrentConnections);
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = AcceptLoopAsync(listener, _slots, _cts.Token);
            return Port;
        }
    }

    public void Stop()
    {
        TcpListener? listener;
        CancellationTokenSource? cts;
        lock (_lock)
        {
            listener = _listener;
            cts = _cts;
            _listener = null;
            _cts = null;
            _slots = null;
            Port = 0;
        }
        if (listener is null) return;
        // Cancel before closing the socket so the accept loop reads this as a stop, not a failure.
        // The source is not disposed: connection tasks may still link to its (cancelled) token.
        cts!.Cancel();
        listener.Stop();
    }

    public void Dispose() => Stop();

    private async Task AcceptLoopAsync(TcpListener listener, SemaphoreSlim slots, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(stop).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.ConnectionReset
                                                or SocketError.ConnectionAborted or SocketError.Interrupted)
            {
                if (stop.IsCancellationRequested) return;
                continue;                                            // client gave up before accept
            }
            catch (Exception e)
            {
                if (stop.IsCancellationRequested) return;
                bool wasCurrent;
                lock (_lock) wasCurrent = ReferenceEquals(_listener, listener);
                if (wasCurrent)
                {
                    Stop();
                    Stopped?.Invoke(e.Message);
                }
                return;
            }

            if (!slots.Wait(0))
            {
                client.Dispose();                                    // saturated: drop, the client retries
                continue;
            }
            _ = ServeAsync(client, slots, stop);
        }
    }

    private static bool IsLoopback(EndPoint? endpoint) =>
        endpoint is IPEndPoint ip && (IPAddress.IsLoopback(ip.Address)
            || (ip.Address.IsIPv4MappedToIPv6 && IPAddress.IsLoopback(ip.Address.MapToIPv4())));

    private async Task ServeAsync(TcpClient client, SemaphoreSlim slots, CancellationToken stop)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
        timeout.CancelAfter(ConnectionTimeout);
        var ct = timeout.Token;
        try
        {
            using (client)
            {
                if (!IsLoopback(client.Client.RemoteEndPoint)) return;
                client.NoDelay = true;
                var stream = client.GetStream();
                var buffer = new MemoryStream();
                var chunk = new byte[ReadChunk];
                while (true)
                {
                    int n = await stream.ReadAsync(chunk, ct).ConfigureAwait(false);
                    if (n > 0) buffer.Write(chunk, 0, n);
                    var parsed = McpHttpRequestParser.Parse(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
                    switch (parsed.State)
                    {
                        case McpHttpRequestParser.State.Incomplete:
                            if (n == 0) return;                      // peer closed mid-request
                            continue;
                        case McpHttpRequestParser.State.Invalid:
                            await SendAsync(client, McpHttpResponse.Error(parsed.Status, parsed.Message!), ct).ConfigureAwait(false);
                            return;
                        default:
                            // The tool itself is not cancelled by the connection budget (as on macOS):
                            // only the wait is, then the connection closes.
                            var response = await _handler.HandleAsync(parsed.Request!, stop).WaitAsync(ct).ConfigureAwait(false);
                            await SendAsync(client, response, ct).ConfigureAwait(false);
                            return;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
        finally
        {
            try { slots.Release(); } catch (ObjectDisposedException) { }
        }
    }

    private static async Task SendAsync(TcpClient client, McpHttpResponse response, CancellationToken ct)
    {
        var stream = client.GetStream();
        await stream.WriteAsync(response.Serialize(), ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        // Graceful close: send FIN, then drain what the client may still send. Closing a Windows
        // socket with unread bytes in its receive buffer sends RST, which can make the client lose
        // the response it has not read yet (e.g. a 413 sent before the oversized body arrived).
        try
        {
            client.Client.Shutdown(SocketShutdown.Send);
            using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drain.CancelAfter(TimeSpan.FromSeconds(2));
            var sink = new byte[8192];
            long drained = 0;
            while (drained < 1024 * 1024)
            {
                int n = await stream.ReadAsync(sink, drain.Token).ConfigureAwait(false);
                if (n <= 0) break;
                drained += n;
            }
        }
        catch (OperationCanceledException) { }
        catch (IOException) { }
        catch (SocketException) { }
    }
}
