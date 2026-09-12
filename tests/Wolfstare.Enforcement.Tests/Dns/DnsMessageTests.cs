using System.Net;
using Wolfstare.Enforcement.Dns;

namespace Wolfstare.Enforcement.Tests.Dns;

public class DnsMessageTests
{
    /// <summary>Builds a DNS query byte by byte, so the tests do not trust the code under test.</summary>
    internal static byte[] Query(
        string name,
        ushort type = DnsMessage.TypeA,
        ushort id = 0xBEEF,
        ushort flags = 0x0100,       // RD set
        ushort questionCount = 1,
        byte[]? additional = null,
        ushort additionalCount = 0)
    {
        var bytes = new List<byte>
        {
            (byte)(id >> 8), (byte)id,
            (byte)(flags >> 8), (byte)flags,
            (byte)(questionCount >> 8), (byte)questionCount,
            0, 0,
            0, 0,
            (byte)(additionalCount >> 8), (byte)additionalCount,
        };

        foreach (var label in name.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
        bytes.Add((byte)(type >> 8));
        bytes.Add((byte)type);
        bytes.Add(0);
        bytes.Add(1); // class IN

        if (additional is not null) bytes.AddRange(additional);
        return bytes.ToArray();
    }

    private static ushort U16(byte[] b, int offset) => (ushort)((b[offset] << 8) | b[offset + 1]);

    private static uint U32(byte[] b, int offset)
        => (uint)((b[offset] << 24) | (b[offset + 1] << 16) | (b[offset + 2] << 8) | b[offset + 3]);

    [Fact]
    public void ParsesAQuery()
    {
        Assert.True(DnsMessage.TryParseQuery(Query("www.reddit.com"), out var q));

        Assert.Equal(0xBEEF, q.Id);
        Assert.Equal("www.reddit.com", q.Name);
        Assert.Equal(DnsMessage.TypeA, q.Type);
        Assert.Equal(1, q.Class);
        Assert.True(q.RecursionDesired);
    }

    [Fact]
    public void NamesAreLowercased()
    {
        Assert.True(DnsMessage.TryParseQuery(Query("WWW.Reddit.COM"), out var q));
        Assert.Equal("www.reddit.com", q.Name);
    }

    [Fact]
    public void RootQueryParsesToEmptyName()
    {
        Assert.True(DnsMessage.TryParseQuery(Query(""), out var q));
        Assert.Equal("", q.Name);
    }

    [Fact]
    public void TruncatedHeaderIsRejected()
        => Assert.False(DnsMessage.TryParseQuery(new byte[] { 0xBE, 0xEF, 0x01, 0x00, 0x00 }, out _));

    [Fact]
    public void TruncatedQuestionIsRejected()
    {
        var query = Query("www.reddit.com");
        Assert.False(DnsMessage.TryParseQuery(query.AsSpan(0, query.Length - 3), out _));
    }

    [Fact]
    public void ResponseIsRejected()
        => Assert.False(DnsMessage.TryParseQuery(Query("reddit.com", flags: 0x8180), out _));

    [Fact]
    public void ZeroQuestionsIsRejected()
        => Assert.False(DnsMessage.TryParseQuery(Query("reddit.com", questionCount: 0), out _));

    [Fact]
    public void CompressedQuestionNameIsRejected()
    {
        // A pointer back to itself at offset 12 would loop forever in a naive parser.
        var query = Query("");
        var withPointer = query[..12].Concat(new byte[] { 0xC0, 0x0C, 0x00, 0x01, 0x00, 0x01 }).ToArray();

        Assert.False(DnsMessage.TryParseQuery(withPointer, out _));
    }

    [Fact]
    public void LabelRunningPastTheEndIsRejected()
    {
        var query = Query("")[..12].Concat(new byte[] { 40, (byte)'a', (byte)'b' }).ToArray();
        Assert.False(DnsMessage.TryParseQuery(query, out _));
    }

    [Fact]
    public void SinkholeAnswerForAIsLoopbackWithShortTtl()
    {
        var query = Query("reddit.com");
        Assert.True(DnsMessage.TryParseQuery(query, out var q));

        var response = DnsMessage.BuildSinkholeResponse(query, q);

        Assert.Equal(0xBEEF, U16(response, 0));
        Assert.True((response[2] & 0x80) != 0, "QR must be set");
        Assert.True((response[2] & 0x01) != 0, "RD must be echoed");
        Assert.True((response[3] & 0x80) != 0, "RA must be set");
        Assert.Equal(0, response[3] & 0x0F);                  // NOERROR
        Assert.Equal(1, U16(response, 4));                    // QDCOUNT
        Assert.Equal(1, U16(response, 6));                    // ANCOUNT
        Assert.Equal(0, U16(response, 10));                   // ARCOUNT

        var questionEnd = query.Length;
        Assert.Equal(query[12..questionEnd], response[12..questionEnd]);

        var a = questionEnd;
        Assert.Equal(0xC00C, U16(response, a));               // pointer to the question name
        Assert.Equal(DnsMessage.TypeA, U16(response, a + 2));
        Assert.Equal(1, U16(response, a + 4));
        Assert.Equal(10u, U32(response, a + 6));
        Assert.Equal(4, U16(response, a + 10));
        Assert.Equal(IPAddress.Loopback, new IPAddress(response[(a + 12)..(a + 16)]));
        Assert.Equal(a + 16, response.Length);
    }

    [Fact]
    public void SinkholeAnswerForAaaaIsIpv6Loopback()
    {
        var query = Query("reddit.com", type: DnsMessage.TypeAaaa);
        Assert.True(DnsMessage.TryParseQuery(query, out var q));

        var response = DnsMessage.BuildSinkholeResponse(query, q);

        var a = query.Length;
        Assert.Equal(DnsMessage.TypeAaaa, U16(response, a + 2));
        Assert.Equal(16, U16(response, a + 10));
        Assert.Equal(IPAddress.IPv6Loopback, new IPAddress(response[(a + 12)..(a + 28)]));
    }

    [Fact]
    public void SinkholeAnswerForOtherTypesHasNoRecords()
    {
        // Browsers send HTTPS (type 65) queries alongside A/AAAA. NOERROR with no answers
        // tells them there is no such record without inviting a retry elsewhere.
        var query = Query("reddit.com", type: 65);
        Assert.True(DnsMessage.TryParseQuery(query, out var q));

        var response = DnsMessage.BuildSinkholeResponse(query, q);

        Assert.Equal(0, response[3] & 0x0F);
        Assert.Equal(0, U16(response, 6));
        Assert.Equal(query.Length, response.Length);
    }

    [Fact]
    public void AdditionalRecordsInTheQueryAreNotEchoed()
    {
        // EDNS clients attach an OPT record. It must not leak into our answer section.
        var opt = new byte[] { 0x00, 0x00, 0x29, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 };
        var query = Query("reddit.com", additional: opt, additionalCount: 1);
        Assert.True(DnsMessage.TryParseQuery(query, out var q));

        var response = DnsMessage.BuildSinkholeResponse(query, q);

        Assert.Equal(0, U16(response, 10));
        Assert.Equal(query.Length - opt.Length + 16, response.Length);
    }

    [Fact]
    public void ServFailSetsRcodeTwoAndNoAnswers()
    {
        var query = Query("example.com");
        Assert.True(DnsMessage.TryParseQuery(query, out var q));

        var response = DnsMessage.BuildServFail(query, q);

        Assert.Equal(0xBEEF, U16(response, 0));
        Assert.Equal(2, response[3] & 0x0F);
        Assert.Equal(0, U16(response, 6));
        Assert.Equal(query.Length, response.Length);
    }
}
