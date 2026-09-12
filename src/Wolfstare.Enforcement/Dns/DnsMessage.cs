using System.Buffers.Binary;
using System.Text;

namespace Wolfstare.Enforcement.Dns;

/// <summary>The first question of a DNS query.</summary>
/// <param name="QuestionEnd">Offset just past the question section, used to echo it verbatim.</param>
public readonly record struct DnsQuestion(
    ushort Id,
    string Name,
    ushort Type,
    ushort Class,
    bool RecursionDesired,
    int QuestionEnd);

/// <summary>
/// The minimum of the DNS wire format (RFC 1035) a sinkhole needs: parse one question, build
/// a loopback answer or a SERVFAIL. Permitted queries are never re-encoded — the upstream's
/// reply is relayed byte for byte — so nothing here has to understand the full format.
///
/// The parser is deliberately strict. This code reads packets from any process on the
/// machine, and anything it cannot parse with certainty is dropped rather than guessed at.
/// </summary>
public static class DnsMessage
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;
    public const ushort ClassInternet = 1;

    /// <summary>
    /// Short enough that ending a session unblocks within seconds despite resolver caching,
    /// long enough not to turn every page load into a fresh lookup.
    /// </summary>
    public const uint SinkholeTtlSeconds = 10;

    private const int HeaderLength = 12;
    private const int MaxNameLength = 253;
    private const byte RcodeNoError = 0;
    private const byte RcodeServFail = 2;

    private static readonly byte[] Ipv4Loopback = [127, 0, 0, 1];
    private static readonly byte[] Ipv6Loopback = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1];

    public static bool TryParseQuery(ReadOnlySpan<byte> message, out DnsQuestion question)
    {
        question = default;
        if (message.Length < HeaderLength) return false;

        var flags = BinaryPrimitives.ReadUInt16BigEndian(message[2..]);
        if ((flags & 0x8000) != 0) return false;           // a response, not a query
        if (((flags >> 11) & 0x0F) != 0) return false;     // opcode other than standard QUERY
        if (BinaryPrimitives.ReadUInt16BigEndian(message[4..]) == 0) return false;

        var position = HeaderLength;
        var name = new StringBuilder();

        while (true)
        {
            if (position >= message.Length) return false;

            int length = message[position];
            if (length == 0)
            {
                position++;
                break;
            }

            // A question name has no reason to be compressed. Rejecting pointers outright
            // removes the pointer-loop class of bug rather than guarding against it.
            if ((length & 0xC0) != 0) return false;
            if (position + 1 + length > message.Length) return false;

            if (name.Length > 0) name.Append('.');

            foreach (var b in message.Slice(position + 1, length))
            {
                if (b is < 0x21 or > 0x7E) return false;
                name.Append((char)(b is >= (byte)'A' and <= (byte)'Z' ? b + 32 : b));
            }

            if (name.Length > MaxNameLength) return false;
            position += 1 + length;
        }

        if (position + 4 > message.Length) return false;

        question = new DnsQuestion(
            BinaryPrimitives.ReadUInt16BigEndian(message),
            name.ToString(),
            BinaryPrimitives.ReadUInt16BigEndian(message[position..]),
            BinaryPrimitives.ReadUInt16BigEndian(message[(position + 2)..]),
            RecursionDesired: (flags & 0x0100) != 0,
            QuestionEnd: position + 4);

        return true;
    }

    /// <summary>
    /// Answers A with 127.0.0.1 and AAAA with ::1, so the browser connects to the local proxy
    /// and sees a block page. Every other type gets NOERROR with no records.
    /// </summary>
    public static byte[] BuildSinkholeResponse(ReadOnlySpan<byte> query, DnsQuestion question)
    {
        var address = question.Class != ClassInternet
            ? null
            : question.Type switch
            {
                TypeA => Ipv4Loopback,
                TypeAaaa => Ipv6Loopback,
                _ => null,
            };

        var answerLength = address is null ? 0 : 12 + address.Length;
        var response = new byte[question.QuestionEnd + answerLength];

        WriteHeader(query, response, RcodeNoError, answerCount: address is null ? (ushort)0 : (ushort)1);
        query[HeaderLength..question.QuestionEnd].CopyTo(response.AsSpan(HeaderLength));

        if (address is not null)
        {
            var answer = response.AsSpan(question.QuestionEnd);
            BinaryPrimitives.WriteUInt16BigEndian(answer, 0xC00C);          // name: pointer to question
            BinaryPrimitives.WriteUInt16BigEndian(answer[2..], question.Type);
            BinaryPrimitives.WriteUInt16BigEndian(answer[4..], ClassInternet);
            BinaryPrimitives.WriteUInt32BigEndian(answer[6..], SinkholeTtlSeconds);
            BinaryPrimitives.WriteUInt16BigEndian(answer[10..], (ushort)address.Length);
            address.CopyTo(answer[12..]);
        }

        return response;
    }

    /// <summary>
    /// Used when no upstream answers. A resolver failure is visible to the user and harmless;
    /// falling back to some unfiltered path would be a bypass.
    /// </summary>
    public static byte[] BuildServFail(ReadOnlySpan<byte> query, DnsQuestion question)
    {
        var response = new byte[question.QuestionEnd];
        WriteHeader(query, response, RcodeServFail, answerCount: 0);
        query[HeaderLength..question.QuestionEnd].CopyTo(response.AsSpan(HeaderLength));
        return response;
    }

    private static void WriteHeader(ReadOnlySpan<byte> query, Span<byte> response, byte rcode, ushort answerCount)
    {
        query[..2].CopyTo(response);                        // ID
        response[2] = (byte)(0x80 | (query[2] & 0x79));     // QR, opcode, RD echoed
        response[3] = (byte)(0x80 | rcode);                 // RA, RCODE

        // Exactly one question is echoed, whatever QDCOUNT the query claimed, and no
        // authority or additional records — an EDNS OPT record must not leak into the reply.
        BinaryPrimitives.WriteUInt16BigEndian(response[4..], 1);
        BinaryPrimitives.WriteUInt16BigEndian(response[6..], answerCount);
        BinaryPrimitives.WriteUInt16BigEndian(response[8..], 0);
        BinaryPrimitives.WriteUInt16BigEndian(response[10..], 0);
    }
}
