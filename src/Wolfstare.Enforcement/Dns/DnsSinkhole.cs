using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;
using Wolfstare.Core.Rules;

namespace Wolfstare.Enforcement.Dns;

/// <summary>
/// A local DNS resolver that answers blocked names with loopback and forwards everything else
/// to the upstream resolvers captured before takeover (spec §7.1).
///
/// UDP only. Clients fall back to TCP when a UDP answer is truncated, and nothing this server
/// generates is ever large enough to truncate; permitted TCP lookups fail, which is visible
/// and harmless, rather than bypassing the rules.
/// </summary>
public sealed class DnsSinkhole(
    RuleSetCache rules,
    IReadOnlyList<IPEndPoint> upstreams,
    ILogger logger,
    TimeSpan? upstreamTimeout = null) : IAsyncDisposable
{
    /// <summary>
    /// SIO_UDP_CONNRESET. Without disabling it, Windows reports an ICMP "port unreachable" from
    /// an earlier send as an exception on the next receive, and a single client that closed its
    /// socket before we replied would take the listener down.
    /// </summary>
    private const IOControlCode UdpConnReset = (IOControlCode)(-1744830452);

    private readonly TimeSpan _upstreamTimeout = upstreamTimeout ?? TimeSpan.FromSeconds(2);
    private readonly CancellationTokenSource _stopping = new();
    private UdpClient? _socket;
    private Task? _receiveLoop;

    /// <summary>Raised with the queried name whenever a lookup is sinkholed.</summary>
    public event Action<string>? Blocked;

    public IPEndPoint LocalEndpoint
        => (IPEndPoint?)_socket?.Client.LocalEndPoint
           ?? throw new InvalidOperationException("The sinkhole has not been started.");

    /// <summary>
    /// Binds and begins serving. Throws <see cref="SocketException"/> if the port is taken —
    /// the caller decides how to degrade (spec §7.4).
    /// </summary>
    public Task StartAsync(IPEndPoint bind, CancellationToken ct)
    {
        if (_socket is not null) throw new InvalidOperationException("The sinkhole is already running.");

        var socket = new UdpClient(bind.AddressFamily);
        try
        {
            // Exclusive use stops another process binding the same port with address reuse
            // and quietly receiving a share of the machine's DNS traffic.
            socket.ExclusiveAddressUse = true;
            socket.Client.IOControl(UdpConnReset, [0, 0, 0, 0], null);
            socket.Client.Bind(bind);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        var local = (IPEndPoint)socket.Client.LocalEndPoint!;
        if (upstreams.Any(u => u.Equals(local)))
        {
            socket.Dispose();

            // Forwarding to ourselves would loop every permitted query until it timed out.
            throw new InvalidOperationException(
                $"Upstream resolvers include the sinkhole's own endpoint {local}.");
        }

        _socket = socket;
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_stopping.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task ReceiveLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await _socket!.ReceiveAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "DNS receive error; continuing.");
                continue;
            }

            // Each query is handled independently so one slow upstream lookup cannot stall
            // answers to every other process on the machine.
            _ = HandleAsync(received, ct);
        }
    }

    private async Task HandleAsync(UdpReceiveResult received, CancellationToken ct)
    {
        try
        {
            var query = received.Buffer;
            if (!DnsMessage.TryParseQuery(query, out var question)) return;

            byte[] response;
            if (rules.Current.EvaluateDomain(question.Name) == Decision.Deny)
            {
                response = DnsMessage.BuildSinkholeResponse(query, question);
                RaiseBlocked(question.Name);
            }
            else
            {
                response = await ForwardAsync(query, question.Id, ct)
                           ?? DnsMessage.BuildServFail(query, question);
            }

            await _socket!.SendAsync(response, received.RemoteEndPoint, ct);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to handle a DNS query from {Client}.", received.RemoteEndPoint);
        }
    }

    /// <summary>
    /// Tries each upstream in order and returns the first reply whose ID matches. A reply with
    /// the wrong ID is discarded — relaying it could hand a client an address meant for a
    /// different query, or one injected by a spoofer. Returns null when every upstream fails.
    /// </summary>
    private async Task<byte[]?> ForwardAsync(byte[] query, ushort id, CancellationToken ct)
    {
        foreach (var upstream in upstreams)
        {
            using var client = new UdpClient(upstream.AddressFamily);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_upstreamTimeout);

            try
            {
                // Connecting filters out datagrams from any address other than the upstream.
                client.Connect(upstream);
                await client.SendAsync(query, timeout.Token);

                while (true)
                {
                    var reply = (await client.ReceiveAsync(timeout.Token)).Buffer;

                    if (reply.Length >= 12
                        && BinaryPrimitives.ReadUInt16BigEndian(reply) == id
                        && (reply[2] & 0x80) != 0)
                    {
                        return reply;
                    }
                }
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                logger.LogDebug("Upstream {Upstream} timed out.", upstream);
            }
            catch (SocketException ex)
            {
                logger.LogDebug(ex, "Upstream {Upstream} failed.", upstream);
            }
        }

        return null;
    }

    private void RaiseBlocked(string name)
    {
        try
        {
            Blocked?.Invoke(name);
        }
        catch (Exception ex)
        {
            // A misbehaving subscriber must not turn a block into a dropped query.
            logger.LogWarning(ex, "A Blocked subscriber threw.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync();
        _socket?.Dispose();

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop; }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        }

        _stopping.Dispose();
    }
}
