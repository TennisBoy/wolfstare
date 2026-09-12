using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wolfstare.Core.Rules;
using Wolfstare.Enforcement.Dns;

namespace Wolfstare.Enforcement.Tests.Dns;

/// <summary>
/// Runs the real sinkhole on an ephemeral loopback port against a fake upstream resolver.
/// No admin rights, no change to the machine's DNS configuration.
/// </summary>
public sealed class DnsSinkholeTests : IAsyncLifetime
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromMilliseconds(250);

    private readonly RuleSetCache _cache = new();
    private readonly FakeUpstream _upstream = new();
    private DnsSinkhole? _sinkhole;

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_sinkhole is not null) await _sinkhole.DisposeAsync();
        _upstream.Dispose();
    }

    private async Task<DnsSinkhole> Start(params IPEndPoint[] upstreams)
    {
        _sinkhole = new DnsSinkhole(
            _cache,
            upstreams.Length == 0 ? [_upstream.Endpoint] : upstreams,
            NullLogger.Instance,
            ShortTimeout);

        await _sinkhole.StartAsync(new IPEndPoint(IPAddress.Loopback, 0), CancellationToken.None);
        return _sinkhole;
    }

    private static async Task<byte[]> Ask(DnsSinkhole sinkhole, byte[] query)
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(query, sinkhole.LocalEndpoint);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await client.ReceiveAsync(cts.Token);
        return result.Buffer;
    }

    private void Block(string pattern, params string[] allow)
        => _cache.Update(new RuleSet(
            [new DomainRule(pattern)],
            allow.Select(a => (BlockRule)new DomainRule(a)).ToList()));

    [Fact]
    public async Task BlockedNameIsSinkholedWithoutContactingUpstream()
    {
        Block("reddit.com");
        var sinkhole = await Start();

        var response = await Ask(sinkhole, DnsMessageTests.Query("www.reddit.com"));

        Assert.Equal(IPAddress.Loopback, new IPAddress(response[^4..]));
        Assert.Equal(0, _upstream.QueriesReceived);
    }

    [Fact]
    public async Task AllowedNameIsForwardedAndTheUpstreamReplyRelayedVerbatim()
    {
        Block("reddit.com");
        var sinkhole = await Start();
        var query = DnsMessageTests.Query("example.com");

        var response = await Ask(sinkhole, query);

        Assert.Equal(FakeUpstream.ReplyTo(query), response);
        Assert.Equal(1, _upstream.QueriesReceived);
    }

    [Fact]
    public async Task AllowlistedSubdomainOfABlockedDomainIsForwarded()
    {
        Block("reddit.com", allow: "old.reddit.com");
        var sinkhole = await Start();
        var query = DnsMessageTests.Query("old.reddit.com");

        Assert.Equal(FakeUpstream.ReplyTo(query), await Ask(sinkhole, query));
    }

    [Fact]
    public async Task SilentUpstreamYieldsServFailRatherThanAnUnfilteredAnswer()
    {
        _upstream.Silent = true;
        var sinkhole = await Start();

        var response = await Ask(sinkhole, DnsMessageTests.Query("example.com"));

        Assert.Equal(2, response[3] & 0x0F);
    }

    [Fact]
    public async Task FallsThroughToTheNextUpstreamWhenOneIsSilent()
    {
        using var silent = new FakeUpstream { Silent = true };
        var sinkhole = await Start(silent.Endpoint, _upstream.Endpoint);
        var query = DnsMessageTests.Query("example.com");

        Assert.Equal(FakeUpstream.ReplyTo(query), await Ask(sinkhole, query));
    }

    [Fact]
    public async Task UpstreamReplyWithTheWrongIdIsIgnored()
    {
        // A reply that does not match the query ID could be a spoofing attempt or a stale
        // answer to another query. Relaying it would hand the client the wrong address.
        _upstream.SendWrongIdFirst = true;
        var sinkhole = await Start();
        var query = DnsMessageTests.Query("example.com");

        Assert.Equal(FakeUpstream.ReplyTo(query), await Ask(sinkhole, query));
    }

    [Fact]
    public async Task BlockedEventFiresWithTheName()
    {
        Block("reddit.com");
        var sinkhole = await Start();
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        sinkhole.Blocked += name => seen.TrySetResult(name);

        await Ask(sinkhole, DnsMessageTests.Query("www.reddit.com"));

        Assert.Equal("www.reddit.com", await seen.Task.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Fact]
    public async Task CacheUpdateTakesEffectWithoutRestart()
    {
        var sinkhole = await Start();
        var query = DnsMessageTests.Query("reddit.com");

        Assert.Equal(FakeUpstream.ReplyTo(query), await Ask(sinkhole, query));

        Block("reddit.com");

        Assert.Equal(IPAddress.Loopback, new IPAddress((await Ask(sinkhole, query))[^4..]));
    }

    [Fact]
    public async Task GarbagePacketIsDroppedAndTheServerKeepsServing()
    {
        Block("reddit.com");
        var sinkhole = await Start();

        using (var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)))
            await client.SendAsync(new byte[] { 1, 2, 3 }, sinkhole.LocalEndpoint);

        var response = await Ask(sinkhole, DnsMessageTests.Query("reddit.com"));
        Assert.Equal(IPAddress.Loopback, new IPAddress(response[^4..]));
    }

    /// <summary>A resolver that answers every query with a recognisable reply.</summary>
    private sealed class FakeUpstream : IDisposable
    {
        private readonly UdpClient _socket = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly CancellationTokenSource _cts = new();
        private int _queries;

        public FakeUpstream() => _ = Task.Run(ServeAsync);

        public IPEndPoint Endpoint => (IPEndPoint)_socket.Client.LocalEndPoint!;

        public int QueriesReceived => Volatile.Read(ref _queries);

        public bool Silent { get; set; }

        public bool SendWrongIdFirst { get; set; }

        /// <summary>The query with QR and RA set and a four-byte marker appended.</summary>
        public static byte[] ReplyTo(byte[] query)
        {
            var reply = query.Concat("UPST"u8.ToArray()).ToArray();
            reply[2] |= 0x80;
            reply[3] |= 0x80;
            return reply;
        }

        private async Task ServeAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var received = await _socket.ReceiveAsync(_cts.Token);
                    Interlocked.Increment(ref _queries);
                    if (Silent) continue;

                    var reply = ReplyTo(received.Buffer);

                    if (SendWrongIdFirst)
                    {
                        var wrong = (byte[])reply.Clone();
                        wrong[0] ^= 0xFF;
                        await _socket.SendAsync(wrong, received.RemoteEndPoint);
                    }

                    await _socket.SendAsync(reply, received.RemoteEndPoint);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
        }

        public void Dispose()
        {
            _cts.Cancel();
            _socket.Dispose();
            _cts.Dispose();
        }
    }
}
