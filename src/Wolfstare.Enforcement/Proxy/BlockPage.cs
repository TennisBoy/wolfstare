using System.Net;
using System.Text;

namespace Wolfstare.Enforcement.Proxy;

/// <summary>Canned HTTP responses the proxy writes.</summary>
internal static class BlockPage
{
    public static readonly byte[] ConnectEstablished = Ascii("HTTP/1.1 200 Connection Established\r\n\r\n");

    public static readonly byte[] ConnectForbidden = Status(403, "Forbidden");

    public static readonly byte[] BadRequest = Status(400, "Bad Request");

    public static readonly byte[] BadGateway = Status(502, "Bad Gateway");

    public static readonly byte[] LoopDetected = Status(508, "Loop Detected");

    /// <summary>
    /// The block page. The host is HTML-encoded because the page renders under the blocked
    /// site's own origin — a raw Host header echoed here would be script injection on that
    /// origin. <c>no-store</c> stops the browser showing a cached block page after unblocking.
    /// </summary>
    public static byte[] Forbidden(string? host)
    {
        var site = WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(host) ? "This site" : host);

        var body = Encoding.UTF8.GetBytes(
            $$"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Blocked by Wolfstare</title>
            <style>
            body{font:16px/1.5 system-ui,sans-serif;display:grid;place-items:center;min-height:90vh;margin:0 16px;background:#111;color:#ccc}
            main{max-width:32rem}h1{font-size:1.4rem;color:#fff}code{color:#fb8}
            </style>
            </head>
            <body><main>
            <h1>Blocked by Wolfstare</h1>
            <p><code>{{site}}</code> is blocked by an active session.</p>
            </main></body>
            </html>
            """);

        var head = Ascii(
            "HTTP/1.1 403 Forbidden\r\n" +
            "Content-Type: text/html; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n");

        return [.. head, .. body];
    }

    private static byte[] Status(int code, string reason)
        => Ascii($"HTTP/1.1 {code} {reason}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    private static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);
}
