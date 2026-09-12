using System.Globalization;
using System.Text;

namespace Wolfstare.Enforcement.Proxy;

/// <summary>
/// The request line and headers of an HTTP/1.x request, read strictly and no further than
/// the blank line that ends them.
///
/// The proxy decides on the target host before any byte reaches an upstream, so parsing has
/// to be unambiguous: anything that could be read two ways — a folded header, userinfo in an
/// authority (<c>allowed.com@blocked.com</c>), an unknown HTTP version — is rejected rather
/// than interpreted.
/// </summary>
public sealed record HttpRequestHead(
    string Method,
    string Target,
    string Version,
    IReadOnlyList<KeyValuePair<string, string>> Headers)
{
    public const int MaxHeadBytes = 16 * 1024;

    private static readonly byte[] HeadTerminator = "\r\n\r\n"u8.ToArray();

    /// <summary>
    /// The target host, lowercased, without brackets or port. Null for an origin-form request
    /// that carried no usable <c>Host</c> header.
    /// </summary>
    public string? Host { get; private init; }

    public int Port { get; private init; }

    public bool IsConnect => Method == "CONNECT";

    /// <summary>True for <c>GET http://host/path</c>, the form a browser sends to an explicit proxy.</summary>
    public bool IsAbsoluteForm { get; private init; }

    /// <summary>The path and query as an origin server expects them.</summary>
    public string OriginFormTarget { get; private init; } = "/";

    /// <summary>
    /// Bytes read past the head — a request body, or a TLS ClientHello sent without waiting for
    /// the CONNECT reply. They belong to the upstream connection and must be forwarded.
    /// </summary>
    public byte[] Remainder { get; private init; } = [];

    public string? GetHeader(string name)
    {
        foreach (var (key, value) in Headers)
            if (key.Equals(name, StringComparison.OrdinalIgnoreCase))
                return value;
        return null;
    }

    /// <summary>
    /// Reads a request head. Returns null when the stream ends first, the head exceeds
    /// <see cref="MaxHeadBytes"/>, or it is malformed.
    /// </summary>
    public static async Task<HttpRequestHead?> ReadAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeadBytes];
        var filled = 0;
        var headEnd = -1;

        while (headEnd < 0)
        {
            if (filled == buffer.Length) return null;

            var read = await stream.ReadAsync(buffer.AsMemory(filled), ct);
            if (read == 0) return null;

            // Search only the new bytes, plus enough overlap to catch a terminator split
            // across two reads.
            var searchFrom = Math.Max(0, filled - (HeadTerminator.Length - 1));
            filled += read;

            var index = buffer.AsSpan(searchFrom, filled - searchFrom).IndexOf(HeadTerminator);
            if (index >= 0) headEnd = searchFrom + index + HeadTerminator.Length;
        }

        var head = Parse(Encoding.Latin1.GetString(buffer, 0, headEnd - HeadTerminator.Length));
        return head is null ? null : head with { Remainder = buffer[headEnd..filled] };
    }

    private static HttpRequestHead? Parse(string text)
    {
        var lines = text.Split("\r\n");

        var requestLine = lines[0].Split(' ');
        if (requestLine.Length != 3) return null;

        var (method, target, version) = (requestLine[0], requestLine[1], requestLine[2]);
        if (method.Length == 0 || !method.All(char.IsAsciiLetterUpper)) return null;
        if (target.Length == 0) return null;
        if (version is not ("HTTP/1.1" or "HTTP/1.0")) return null;

        var headers = new List<KeyValuePair<string, string>>(lines.Length - 1);
        foreach (var line in lines.AsSpan(1))
        {
            // Obsolete line folding lets one header be read as two by different parsers.
            if (line.Length == 0 || line[0] is ' ' or '\t') return null;

            var colon = line.IndexOf(':');
            if (colon <= 0) return null;

            var name = line[..colon];
            if (name.Any(c => c is ' ' or '\t')) return null;

            headers.Add(new(name, line[(colon + 1)..].Trim()));
        }

        var head = new HttpRequestHead(method, target, version, headers);
        return method == "CONNECT" ? ResolveConnect(head) : ResolveRequest(head);
    }

    private static HttpRequestHead? ResolveConnect(HttpRequestHead head)
        => TryParseAuthority(head.Target, defaultPort: null, out var host, out var port)
            ? head with { Host = host, Port = port, OriginFormTarget = head.Target }
            : null;

    private static HttpRequestHead? ResolveRequest(HttpRequestHead head)
    {
        const string httpScheme = "http://";

        if (head.Target.StartsWith(httpScheme, StringComparison.OrdinalIgnoreCase))
        {
            var rest = head.Target[httpScheme.Length..];
            var pathStart = rest.IndexOfAny(['/', '?']);
            var authority = pathStart < 0 ? rest : rest[..pathStart];

            var origin = pathStart < 0 ? "/" : rest[pathStart..];
            if (origin[0] == '?') origin = "/" + origin;

            return TryParseAuthority(authority, 80, out var host, out var port)
                ? head with { Host = host, Port = port, IsAbsoluteForm = true, OriginFormTarget = origin }
                : null;
        }

        // Anything that is neither absolute http nor origin-form (https://, a bare word) is
        // not a request this proxy can reason about.
        if (head.Target[0] != '/' && head.Target != "*") return null;

        var hostHeader = head.GetHeader("Host");
        if (hostHeader is not null && TryParseAuthority(hostHeader, 80, out var h, out var p))
            return head with { Host = h, Port = p, OriginFormTarget = head.Target };

        return head with { Port = 80, OriginFormTarget = head.Target };
    }

    /// <summary>Parses <c>host</c>, <c>host:port</c>, or <c>[v6]:port</c>.</summary>
    /// <param name="defaultPort">Null when a port is mandatory, as for CONNECT.</param>
    private static bool TryParseAuthority(string authority, int? defaultPort, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        // Userinfo is refused outright: "allowed.com@blocked.com" names blocked.com, and a
        // parser that got that wrong would be a bypass.
        if (authority.Length == 0 || authority.Contains('@')) return false;

        string hostPart;
        string? portPart = null;

        if (authority[0] == '[')
        {
            var close = authority.IndexOf(']');
            if (close < 0) return false;

            hostPart = authority[1..close];
            var after = authority[(close + 1)..];
            if (after.Length > 0)
            {
                if (after[0] != ':') return false;
                portPart = after[1..];
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                hostPart = authority[..colon];
                portPart = authority[(colon + 1)..];
            }
            else
            {
                hostPart = authority;
            }
        }

        if (hostPart.Length == 0) return false;

        if (portPart is null)
        {
            if (defaultPort is null) return false;
            port = defaultPort.Value;
        }
        else if (!int.TryParse(portPart, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                 || port is < 1 or > 65535)
        {
            return false;
        }

        host = hostPart.ToLowerInvariant();
        return true;
    }
}
