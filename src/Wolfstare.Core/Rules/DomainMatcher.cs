using System.Globalization;

namespace Wolfstare.Core.Rules;

/// <summary>
/// Host normalisation and pattern matching for <see cref="DomainRule"/>.
///
/// Semantics:
/// <list type="bullet">
///   <item><c>*</c> matches every host.</item>
///   <item><c>reddit.com</c> matches the apex and every subdomain — what a user expects
///         when they type a bare domain.</item>
///   <item><c>*.reddit.com</c> matches subdomains only, not the apex.</item>
/// </list>
///
/// This runs on every DNS lookup, so it allocates as little as it reasonably can.
/// </summary>
public static class DomainMatcher
{
    private static readonly IdnMapping Idn = new() { AllowUnassigned = true, UseStd3AsciiRules = false };

    /// <summary>Lowercases, trims, strips a trailing dot, and converts to punycode.</summary>
    public static string Normalize(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return string.Empty;

        var trimmed = host.Trim().TrimEnd('.');
        if (trimmed.Length == 0) return string.Empty;

        try
        {
            return Idn.GetAscii(trimmed).ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            // Not a valid IDN. Fall back to a plain lowercase comparison rather than
            // throwing: an unparseable host must still be matched against patterns, never
            // silently permitted.
            return trimmed.ToLowerInvariant();
        }
    }

    /// <summary>True when <paramref name="host"/> is covered by <paramref name="pattern"/>.</summary>
    public static bool Matches(string pattern, string host)
    {
        if (string.IsNullOrWhiteSpace(pattern)) return false;

        var p = pattern.Trim();
        if (p == "*") return !string.IsNullOrWhiteSpace(host);

        var h = Normalize(host);
        if (h.Length == 0) return false;

        if (p.StartsWith("*.", StringComparison.Ordinal))
        {
            var suffix = Normalize(p[2..]);
            return suffix.Length != 0 && IsProperSubdomainOf(h, suffix);
        }

        var bare = Normalize(p);
        if (bare.Length == 0) return false;

        return h.Equals(bare, StringComparison.Ordinal) || IsProperSubdomainOf(h, bare);
    }

    /// <summary>
    /// True when <paramref name="host"/> sits strictly beneath <paramref name="parent"/>.
    /// The label-boundary check is what stops <c>reddit.com</c> matching <c>notreddit.com</c>.
    /// </summary>
    private static bool IsProperSubdomainOf(string host, string parent)
        => host.Length > parent.Length + 1
           && host.EndsWith(parent, StringComparison.Ordinal)
           && host[host.Length - parent.Length - 1] == '.';
}
