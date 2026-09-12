using System.Text;
using Wolfstare.Enforcement.Proxy;

namespace Wolfstare.Enforcement.Tests.Proxy;

public class HttpRequestHeadTests
{
    private static Task<HttpRequestHead?> Read(string raw)
        => HttpRequestHead.ReadAsync(new MemoryStream(Encoding.ASCII.GetBytes(raw)), CancellationToken.None);

    [Fact]
    public async Task ParsesConnect()
    {
        var head = await Read("CONNECT www.reddit.com:443 HTTP/1.1\r\nHost: www.reddit.com:443\r\n\r\n");

        Assert.NotNull(head);
        Assert.Equal("CONNECT", head.Method);
        Assert.Equal("www.reddit.com", head.Host);
        Assert.Equal(443, head.Port);
    }

    [Fact]
    public async Task ParsesIpv6ConnectTarget()
    {
        var head = await Read("CONNECT [::1]:8443 HTTP/1.1\r\n\r\n");

        Assert.NotNull(head);
        Assert.Equal("::1", head.Host);
        Assert.Equal(8443, head.Port);
    }

    [Fact]
    public async Task ConnectWithoutAPortIsMalformed()
        => Assert.Null(await Read("CONNECT www.reddit.com HTTP/1.1\r\n\r\n"));

    [Fact]
    public async Task ParsesAbsoluteFormAndExposesOriginForm()
    {
        var head = await Read("GET http://example.com:8081/a?b=1 HTTP/1.1\r\nHost: example.com\r\n\r\n");

        Assert.NotNull(head);
        Assert.Equal("example.com", head.Host);
        Assert.Equal(8081, head.Port);
        Assert.Equal("/a?b=1", head.OriginFormTarget);
    }

    [Fact]
    public async Task OriginFormTakesHostAndPortFromTheHostHeader()
    {
        var head = await Read("GET /x HTTP/1.1\r\nHost: Example.COM:8080\r\n\r\n");

        Assert.NotNull(head);
        Assert.Equal("example.com", head.Host);
        Assert.Equal(8080, head.Port);
        Assert.Equal("/x", head.OriginFormTarget);
    }

    [Fact]
    public async Task OriginFormDefaultsToPort80()
    {
        var head = await Read("GET / HTTP/1.1\r\nHost: example.com\r\n\r\n");

        Assert.Equal(80, head!.Port);
    }

    [Fact]
    public async Task MissingHostLeavesHostNull()
    {
        var head = await Read("GET / HTTP/1.1\r\n\r\n");

        Assert.NotNull(head);
        Assert.Null(head.Host);
    }

    [Fact]
    public async Task HeaderLookupIsCaseInsensitive()
    {
        var head = await Read("GET / HTTP/1.1\r\nHOST: example.com\r\nX-Thing: a b\r\n\r\n");

        Assert.Equal("example.com", head!.GetHeader("host"));
        Assert.Equal("a b", head.GetHeader("x-thing"));
        Assert.Null(head.GetHeader("absent"));
    }

    [Fact]
    public async Task BytesAfterTheHeadArePreserved()
    {
        // Whatever the client sent beyond the head belongs to the upstream connection. Losing
        // it would corrupt a POST body or the first bytes of a TLS handshake.
        var head = await Read("POST http://example.com/ HTTP/1.1\r\nHost: example.com\r\n\r\nBODY");

        Assert.Equal("BODY", Encoding.ASCII.GetString(head!.Remainder));
    }

    [Fact]
    public async Task HeadSplitAcrossManyReadsIsReassembled()
    {
        var raw = Encoding.ASCII.GetBytes("GET / HTTP/1.1\r\nHost: example.com\r\n\r\nX");

        var stream = new TrickleStream(raw);
        var head = await HttpRequestHead.ReadAsync(stream, CancellationToken.None);

        Assert.NotNull(head);
        Assert.Equal("example.com", head.Host);

        // Reading stops at the terminator, so a byte after it may still be in the stream rather
        // than in Remainder. The invariant is that it is in exactly one of the two — never lost,
        // never duplicated.
        using var rest = new MemoryStream();
        await stream.CopyToAsync(rest);
        Assert.Equal("X", Encoding.ASCII.GetString([.. head.Remainder, .. rest.ToArray()]));
    }

    [Fact]
    public async Task OversizedHeadIsRejected()
        => Assert.Null(await Read("GET / HTTP/1.1\r\nX-Big: " + new string('a', 20_000) + "\r\n\r\n"));

    [Theory]
    [InlineData("NONSENSE\r\n\r\n")]
    [InlineData("GET /\r\n\r\n")]
    [InlineData("GET / FTP/1.0\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\nNoColonHere\r\n\r\n")]
    [InlineData("GET / HTTP/1.1\r\n folded: header\r\n\r\n")]
    public async Task MalformedHeadsAreRejected(string raw)
        => Assert.Null(await Read(raw));

    [Fact]
    public async Task StreamEndingBeforeTheHeadCompletesIsRejected()
        => Assert.Null(await Read("GET / HTTP/1.1\r\nHost: exam"));

    /// <summary>Returns one byte per read, to exercise boundary detection across chunks.</summary>
    private sealed class TrickleStream(byte[] data) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= data.Length || count == 0) return 0;
            buffer[offset] = data[_position++];
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
