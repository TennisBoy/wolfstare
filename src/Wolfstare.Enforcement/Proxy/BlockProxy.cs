using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Wolfstare.Core.Rules;

namespace Wolfstare.Enforcement.Proxy;

/// <summary>
/// The loopback proxy (spec §7.3). Three kinds of listener share one rule set:
/// <list type="bullet">
///   <item><b>Explicit</b> — registered as the system proxy. Decides on the CONNECT target or
///         absolute-form host, then tunnels or forwards permitted traffic.</item>
///   <item><b>Transparent HTTP</b> — port 80, reached only because the sinkhole answered a
///         blocked name with 127.0.0.1. Always serves the block page.</item>
///   <item><b>Transparent TLS</b> — port 443, likewise. Without a trusted certificate there is
///         nothing useful to send, so it closes.</item>
/// </list>
/// The transparent listeners never forward. A request only arrives there because its name
/// resolved to loopback, so forwarding would resolve it to loopback again and loop.
/// </summary>
public sealed class BlockProxy(RuleSetCache rules, ILogger logger, ICertificateProvider certificates)
    : IAsyncDisposable
{
    private static readonly TimeSpan HeadTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DialTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan HalfCloseGrace = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Stripped before forwarding. <c>Connection: close</c> is then forced so every upstream
    /// connection carries exactly one request — otherwise a keep-alive connection opened for a
    /// permitted host could carry a later request for a blocked one past the rule check.
    /// </summary>
    private static readonly HashSet<string> StrippedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Proxy-Connection", "Keep-Alive", "Proxy-Authorization",
    };

    private readonly CancellationTokenSource _stopping = new();
    private readonly object _gate = new();
    private readonly List<TcpListener> _listeners = [];
    private readonly List<Task> _acceptLoops = [];

    /// <summary>Raised with the host whenever a request is refused.</summary>
    public event Action<string>? Blocked;

    public Task<IPEndPoint> StartExplicitAsync(IPEndPoint bind, CancellationToken ct)
        => StartListener(bind, HandleExplicitAsync);

    public Task<IPEndPoint> StartTransparentHttpAsync(IPEndPoint bind, CancellationToken ct)
        => StartListener(bind, HandleTransparentHttpAsync);

    public Task<IPEndPoint> StartTransparentTlsAsync(IPEndPoint bind, CancellationToken ct)
        => StartListener(bind, HandleTransparentTlsAsync);

    /// <summary>Throws <see cref="SocketException"/> if the port is taken.</summary>
    private Task<IPEndPoint> StartListener(IPEndPoint bind, Func<TcpClient, CancellationToken, Task> handler)
    {
        ObjectDisposedException.ThrowIf(_stopping.IsCancellationRequested, this);

        var listener = new TcpListener(bind) { ExclusiveAddressUse = true };
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;

        lock (_gate)
        {
            _listeners.Add(listener);
            _acceptLoops.Add(Task.Run(
                () => AcceptLoopAsync(listener, handler, _stopping.Token), CancellationToken.None));
        }

        return Task.FromResult(endpoint);
    }

    private async Task AcceptLoopAsync(
        TcpListener listener, Func<TcpClient, CancellationToken, Task> handler, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                if (ct.IsCancellationRequested) break;
                logger.LogDebug(ex, "Proxy accept failed; continuing.");
                continue;
            }

            _ = HandleSafelyAsync(client, handler, ct);
        }
    }

    private async Task HandleSafelyAsync(
        TcpClient client, Func<TcpClient, CancellationToken, Task> handler, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await handler(client, ct);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
            {
                logger.LogDebug(ex, "Proxy connection ended abruptly.");
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Proxy connection handler failed.");
            }
        }
    }

    private async Task HandleExplicitAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var head = await ReadHeadAsync(stream, ct);

        if (head?.Host is null || !(head.IsConnect || head.IsAbsoluteForm))
        {
            await RespondAndCloseAsync(client, BlockPage.BadRequest, ct);
            return;
        }

        if (rules.Current.EvaluateDomain(head.Host) == Decision.Deny)
        {
            RaiseBlocked(head.Host);
            await RespondAndCloseAsync(
                client, head.IsConnect ? BlockPage.ConnectForbidden : BlockPage.Forbidden(head.Host), ct);
            return;
        }

        using var upstream = await DialAsync(head.Host, head.Port, ct);
        if (upstream is null)
        {
            await RespondAndCloseAsync(client, BlockPage.BadGateway, ct);
            return;
        }

        // A permitted name that resolves back to one of our own listeners would have the proxy
        // connecting to itself until it ran out of sockets.
        if (IsOwnEndpoint(upstream.RemoteEndPoint))
        {
            await RespondAndCloseAsync(client, BlockPage.LoopDetected, ct);
            return;
        }

        await using var upstreamStream = new NetworkStream(upstream, ownsSocket: false);

        if (head.IsConnect)
            await WriteAsync(stream, BlockPage.ConnectEstablished, ct);
        else
            await upstreamStream.WriteAsync(BuildOriginRequest(head), ct);

        if (head.Remainder.Length > 0)
            await upstreamStream.WriteAsync(head.Remainder, ct);

        await PipeAsync(client.Client, stream, upstream, upstreamStream, ct);
    }

    private async Task HandleTransparentHttpAsync(TcpClient client, CancellationToken ct)
    {
        var head = await ReadHeadAsync(client.GetStream(), ct);
        var host = head?.Host;

        if (host is not null) RaiseBlocked(host);
        await RespondAndCloseAsync(client, BlockPage.Forbidden(host), ct);
    }

    private async Task HandleTransparentTlsAsync(TcpClient client, CancellationToken ct)
    {
        // Without inspection there is no certificate the browser would accept for the blocked
        // name, so the connection error it sees is the acknowledged v1 cost (spec §7.3). When a
        // provider can inspect, this is where the TLS handshake and block page will go.
        if (certificates.CanInspect)
            logger.LogDebug("HTTPS inspection is available but not implemented; closing.");

        await CloseGracefullyAsync(client, ct);
    }

    private static async Task<HttpRequestHead?> ReadHeadAsync(Stream stream, CancellationToken ct)
    {
        // A client that opens a connection and never finishes its head would otherwise hold a
        // handler open indefinitely.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(HeadTimeout);

        try
        {
            return await HttpRequestHead.ReadAsync(stream, timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task<Socket?> DialAsync(string host, int port, CancellationToken ct)
    {
        // Dual-mode, so a name resolving to either address family can be reached.
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(DialTimeout);

        try
        {
            await socket.ConnectAsync(host, port, timeout.Token);
            return socket;
        }
        catch (Exception ex) when (ex is SocketException
                                   || (ex is OperationCanceledException && !ct.IsCancellationRequested))
        {
            logger.LogDebug(ex, "Could not reach {Host}:{Port}.", host, port);
            socket.Dispose();
            return null;
        }
    }

    private bool IsOwnEndpoint(EndPoint? remote)
    {
        if (remote is not IPEndPoint ip) return false;

        var address = ip.Address.IsIPv4MappedToIPv6 ? ip.Address.MapToIPv4() : ip.Address;
        if (!IPAddress.IsLoopback(address)) return false;

        lock (_gate)
            return _listeners.Any(l => ((IPEndPoint)l.LocalEndpoint).Port == ip.Port);
    }

    private static byte[] BuildOriginRequest(HttpRequestHead head)
    {
        var request = new StringBuilder()
            .Append(head.Method).Append(' ').Append(head.OriginFormTarget).Append(' ').Append(head.Version)
            .Append("\r\n");

        var sawHost = false;
        foreach (var (name, value) in head.Headers)
        {
            if (StrippedHeaders.Contains(name)) continue;
            if (name.Equals("Host", StringComparison.OrdinalIgnoreCase)) sawHost = true;
            request.Append(name).Append(": ").Append(value).Append("\r\n");
        }

        if (!sawHost)
        {
            var host = head.Host!.Contains(':') ? $"[{head.Host}]" : head.Host;
            request.Append("Host: ").Append(host);
            if (head.Port != 80) request.Append(':').Append(head.Port);
            request.Append("\r\n");
        }

        request.Append("Connection: close\r\n\r\n");
        return Encoding.Latin1.GetBytes(request.ToString());
    }

    private static async Task PipeAsync(
        Socket clientSocket, Stream clientStream, Socket upstreamSocket, Stream upstreamStream, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var toUpstream = PumpAsync(clientStream, upstreamStream, upstreamSocket, linked.Token);
        var toClient = PumpAsync(upstreamStream, clientStream, clientSocket, linked.Token);

        var first = await Task.WhenAny(toUpstream, toClient);
        var second = first == toUpstream ? toClient : toUpstream;

        // One side has finished and its FIN has been passed on. Give the other a bounded time
        // to finish its reply, then tear the tunnel down.
        await Task.WhenAny(second, Task.Delay(HalfCloseGrace, linked.Token));
        await linked.CancelAsync();
        await second;
    }

    private static async Task PumpAsync(Stream from, Stream to, Socket toSocket, CancellationToken ct)
    {
        try
        {
            await from.CopyToAsync(to, ct);
            toSocket.Shutdown(SocketShutdown.Send);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task RespondAndCloseAsync(TcpClient client, byte[] response, CancellationToken ct)
    {
        await WriteAsync(client.GetStream(), response, ct);
        await CloseGracefullyAsync(client, ct);
    }

    /// <summary>
    /// Sends FIN, then discards whatever the peer is still sending until it closes or a short
    /// timeout passes. Closing a socket with unread data makes Windows send RST, and a client
    /// can receive that reset before it reads the response just written to it.
    /// </summary>
    private static async Task CloseGracefullyAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            // The stream must be taken before shutting down: Socket.Shutdown marks the socket
            // disconnected, after which TcpClient.GetStream throws. That throw disposed the
            // socket with the peer's bytes unread — producing exactly the reset this method
            // exists to prevent.
            var stream = client.GetStream();
            client.Client.Shutdown(SocketShutdown.Send);

            using var drain = CancellationTokenSource.CreateLinkedTokenSource(ct);
            drain.CancelAfter(DrainTimeout);

            var buffer = new byte[4096];
            while (await stream.ReadAsync(buffer, drain.Token) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private static async Task WriteAsync(Stream stream, byte[] bytes, CancellationToken ct)
    {
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private void RaiseBlocked(string host)
    {
        try
        {
            Blocked?.Invoke(host);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "A Blocked subscriber threw.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();

        TcpListener[] listeners;
        Task[] loops;
        lock (_gate)
        {
            listeners = [.. _listeners];
            loops = [.. _acceptLoops];
        }

        foreach (var listener in listeners) listener.Stop();
        await Task.WhenAll(loops);

        _stopping.Dispose();
    }
}
