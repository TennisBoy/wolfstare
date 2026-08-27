namespace Wolfstare.Core.Rules;

/// <summary>A single blocking rule. Closed hierarchy — see spec §4.1.</summary>
public abstract record BlockRule;

/// <summary>
/// Blocks a domain. The pattern may be <c>*</c>, <c>example.com</c>, or <c>*.example.com</c>.
/// </summary>
public sealed record DomainRule(string Pattern) : BlockRule
{
    public bool Matches(string host) => DomainMatcher.Matches(Pattern, host);
}

/// <summary>Blocks an application by identity rather than by install location.</summary>
public sealed record AppRule(AppMatcher Matcher) : BlockRule
{
    public bool Matches(ProcessIdentity identity) => Matcher.Matches(identity);
}

/// <summary>
/// Blocks a specific URL path. Modelled now so the domain does not change when HTTPS
/// inspection lands, but rejected by <see cref="RuleValidator"/> in v1 (spec §4.1) because
/// deciding on a path requires decrypting the request.
/// </summary>
public sealed record PathRule(string UrlPattern) : BlockRule;
