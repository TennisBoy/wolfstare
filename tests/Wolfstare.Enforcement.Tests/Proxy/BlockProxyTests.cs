using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using Wolfstare.Core.Rules;
using Wolfstare.Enforcement.Proxy;

namespace Wolfstare.Enforcement.Tests.Proxy;

/// <summary>
/// Runs the real proxy on ephemeral loopback ports against a fake origin server. No system
/// proxy settings are touched.
/// </summary>
public sealed class BlockProxyTests : IAsyncLifetime
{
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(5);

    private readonly RuleSetCache _cache = new();
    private readonly ListLogger _log = new();
    private readonly BlockProxy _proxy;

    public BlockProxyTests()
        => _proxy = new BlockProxy(_cache, _log, new NullCertificateProvider());

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        await _proxy.DisposeAsync();

        // Handlers swallow failures so one bad client cannot hurt another, which also hides
        // bugs. A warning means a handler died unexpectedly, so it fails the test.
        var warnings = _log.AtLeast(LogLevel.Warning);
        Assert.True(warnings.Count == 0, string.Join(Environment.NewLine, warnings));
    }

    private static IPEndPoint AnyLoopback => new(IPAddress.Loopback, 0);

    private void Block(params string[] patterns)
        => _cache.Update(new RuleSet(patterns.Select(p => (BlockRule)new DomainRule(p)).ToList(), []));

    private static async Task<(TcpClient Client, NetworkStream Stream)> Connect(IPEndPoint endpoint)
    {
        var client = new TcpClient();
        await client.ConnectAsync(endpoint);
        return (client, client.GetStream());
    }

    private static async Task Send(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.ASCII.GetBytes(text));
        await stream.FlushAsync();
    }

    /// <summary>Reads until the peer closes the connection.</summary>
    private static async Task<string> ReadToEnd(Stream stream)
    {
        using var cts = new CancellationTokenSource(ReadTimeout);
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, cts.Token);
        return Encoding.ASCII.GetString(buffer.ToArray());
    }

    /// <summary>Reads exactly the given number of bytes.</summary>
    private static async Task<string> ReadExactly(Stream stream, int count)
    {
        using var cts = new CancellationTokenSource(ReadTimeout);
        var buffer = new byte[count];
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return Encoding.ASCII.GetString(buffer);
    }

    /// <summary>Reads through the blank line that ends an HTTP response head.</summary>
    private static async Task<string> ReadHead(Stream stream)
    {
        using var cts = new CancellationTokenSource(ReadTimeout);
        var head = new StringBuilder();
        var one = new byte[1];

        while (!head.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
        {
            if (await stream.ReadAsync(one, cts.Token) == 0) break;
            head.Append((char)one[0]);
        }

        return head.ToString();
    }

    // ---- explicit proxy: CONNECT ----

    [Fact]
    public async Task AllowedConnectTunnelsBytesInBothDirections()
    {
        await using var origin = FakeOrigin.Echo();
        var proxy = await _proxy.StartExplicitAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(proxy);
        using var _ = client;

        await Send(stream, $"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\nHost: 127.0.0.1:{origin.Port}\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 200", await ReadHead(stream));

        await Send(stream, "ping");
        Assert.Equal("ping", await ReadExactly(stream, 4));
        Assert.Equal(1, origin.Accepted);
    }

    [Fact]
    public async Task BytesSentWithTheConnectHeadAreForwarded()
    {
        // Some clients send the TLS ClientHello without waiting for the 200.
        await using var origin = FakeOrigin.Echo();
        var proxy = await _proxy.StartExplicitAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(proxy);
        using var _ = client;

        await Send(stream, $"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\n\r\nearly");

        Assert.StartsWith("HTTP/1.1 200", await ReadHead(stream));
        Assert.Equal("early", await ReadExactly(stream, 5));
    }

    [Fact]
    public async Task BlockedConnectIsRefusedWithoutDialingTheOrigin()
    {
        await using var origin = FakeOrigin.Echo();
        Block("127.0.0.1");
        var proxy = await _proxy.StartExplicitAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(proxy);
        using var _ = client;

        await Send(stream, $"CONNECT 127.0.0.1:{origin.Port} HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 403", await ReadToEnd(stream));
        Assert.Equal(0, origin.Accepted);
    }

    [Fact]
    public async Task ConnectToAnUnreachableOriginIsABadGateway()
    {
        var deadPort = FakeOrigin.UnusedPort();
        var proxy = await _proxy.StartExplicitAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(proxy);
        using var _ = client;

        await Send(stream, $"CONNECT 127.0.0.1:{deadPort} HTTP/1.1\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 502", await ReadToEnd(stream));
    }

    [Fact]
    public async Task BlockedEventFiresWithTheHost()
    {
        Block("reddit.com");
        var seen = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _proxy.Blocked += host => seen.TrySetResult(host);
        var proxy = await _proxy.StartExplicitAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(proxy);
        using var _ = client;

        await Send(stream, "CONNECT www.reddit.com:443 HTTP/1.1\r\n\r\n");

        Assert.Equal("www.reddit.com", await seen.Task.WaitAsync(ReadTimeout));
    }

    // ---- explicit proxy: plain HTTP ----

    [Fact]
    public async Task AllowedAbsoluteFormRequestReachesTheOriginInOriginForm()
    {
        await using var origin = FakeOrigin.Http();
        var proxy = await _proxy.StartExplicitAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(proxy);
        using var _ = client;

        await Send(stream,
            $"GET http://127.0.0.1:{origin.Port}/hello?x=1 HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{origin.Port}\r\n" +
            "Proxy-Connection: keep-alive\r\n" +
            "Connection: keep-alive\r\n\r\n");

        var response = await ReadToEnd(stream);
        var seen = await origin.FirstHead.WaitAsync(ReadTimeout);

        Assert.StartsWith("GET /hello?x=1 HTTP/1.1\r\n", seen);
        Assert.DoesNotContain("Proxy-Connection", seen, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\r\nConnection: close\r\n", seen);
        Assert.DoesNotContain("keep-alive", seen, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ok", response);
    }

    [Fact]
    public async Task BlockedHttpRequestGetsTheBlockPage()
    {
        Block("reddit.com");
        var proxy = await _proxy.StartExplicitAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(proxy);
        using var _ = client;

        await Send(stream, "GET http://www.reddit.com/r/all HTTP/1.1\r\nHost: www.reddit.com\r\n\r\n");

        var response = await ReadToEnd(stream);
        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Contains("Content-Type: text/html", response);
        Assert.Contains("www.reddit.com", response);
    }

    [Fact]
    public async Task MalformedRequestGetsBadRequest()
    {
        var proxy = await _proxy.StartExplicitAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(proxy);
        using var _ = client;

        await Send(stream, "NONSENSE\r\n\r\n");

        Assert.StartsWith("HTTP/1.1 400", await ReadToEnd(stream));
    }

    [Fact]
    public async Task OversizedHeadGetsBadRequest()
    {
        var proxy = await _proxy.StartExplicitAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(proxy);
        using var _ = client;

        await Send(stream, "GET / HTTP/1.1\r\nX-Big: " + new string('a', 20_000));

        Assert.StartsWith("HTTP/1.1 400", await ReadToEnd(stream));
    }

    // ---- transparent listeners ----

    [Fact]
    public async Task TransparentHttpListenerBlocksEvenAPermittedHost()
    {
        // Traffic only arrives here because the sinkhole redirected it. Forwarding would
        // resolve the name back to 127.0.0.1 and loop.
        var endpoint = await _proxy.StartTransparentHttpAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(endpoint);
        using var _ = client;

        await Send(stream, "GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");

        var response = await ReadToEnd(stream);
        Assert.StartsWith("HTTP/1.1 403", response);
        Assert.Contains("example.com", response);
    }

    [Fact]
    public async Task BlockPageHtmlEncodesTheHost()
    {
        // The page is rendered under the blocked site's origin, so echoing a raw Host header
        // would be a script injection on that origin.
        var endpoint = await _proxy.StartTransparentHttpAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(endpoint);
        using var _ = client;

        await Send(stream, "GET / HTTP/1.1\r\nHost: <b>x</b>.example\r\n\r\n");

        var response = await ReadToEnd(stream);
        Assert.Contains("&lt;b&gt;", response);
        Assert.DoesNotContain("<b>x</b>", response);
    }

    [Fact]
    public async Task TransparentTlsListenerClosesWithoutWriting()
    {
        var endpoint = await _proxy.StartTransparentTlsAsync(AnyLoopback, CancellationToken.None);
        var (client, stream) = await Connect(endpoint);
        using var _ = client;

        await Send(stream, "\x16\x03\x01fake-client-hello");

        Assert.Equal("", await ReadToEnd(stream));
    }

    [Fact]
    public void NullCertificateProviderCannotInspect()
        => Assert.False(new NullCertificateProvider().CanInspect);

    /// <summary>A TCP server that either echoes bytes or answers one HTTP request.</summary>
    private sealed class FakeOrigin : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource<string> _firstHead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly bool _http;
        private int _accepted;

        private FakeOrigin(bool http)
        {
            _http = http;
            _listener.Start();
            _ = Task.Run(AcceptLoopAsync);
        }

        public static FakeOrigin Echo() => new(http: false);

        public static FakeOrigin Http() => new(http: true);

        public static int UnusedPort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Accepted => Volatile.Read(ref _accepted);

        public Task<string> FirstHead => _firstHead.Task;

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    Interlocked.Increment(ref _accepted);
                    _ = Task.Run(() => ServeAsync(client));
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();

            try
            {
                if (!_http)
                {
                    await stream.CopyToAsync(stream, _cts.Token);
                    return;
                }

                _firstHead.TrySetResult(await ReadHead(stream));
                await stream.WriteAsync(
                    "HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"u8.ToArray(), _cts.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            _cts.Dispose();
        }
    }
}
