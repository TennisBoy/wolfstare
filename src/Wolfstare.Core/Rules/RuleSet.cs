namespace Wolfstare.Core.Rules;

/// <summary>The outcome of evaluating a host or process against a <see cref="RuleSet"/>.</summary>
public enum Decision
{
    Permit,
    Deny,
}

/// <summary>
/// A set of block rules plus an allowlist that overrides them.
///
/// Evaluation order is allowlist first, which is what makes "block everything except these"
/// expressible as a single <c>DomainRule("*")</c> with a populated allowlist (spec §4.2).
/// </summary>
public sealed record RuleSet(IReadOnlyList<BlockRule> Rules, IReadOnlyList<BlockRule> Allowlist)
{
    public static RuleSet Empty { get; } = new([], []);

    public Decision EvaluateDomain(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return Decision.Permit;
        if (AnyDomainMatch(Allowlist, host)) return Decision.Permit;
        return AnyDomainMatch(Rules, host) ? Decision.Deny : Decision.Permit;
    }

    public Decision EvaluateApp(ProcessIdentity identity)
    {
        if (AnyAppMatch(Allowlist, identity)) return Decision.Permit;
        return AnyAppMatch(Rules, identity) ? Decision.Deny : Decision.Permit;
    }

    private static bool AnyDomainMatch(IReadOnlyList<BlockRule> rules, string host)
    {
        for (var i = 0; i < rules.Count; i++)
            if (rules[i] is DomainRule d && d.Matches(host))
                return true;
        return false;
    }

    private static bool AnyAppMatch(IReadOnlyList<BlockRule> rules, ProcessIdentity identity)
    {
        for (var i = 0; i < rules.Count; i++)
            if (rules[i] is AppRule a && a.Matches(identity))
                return true;
        return false;
    }
}
