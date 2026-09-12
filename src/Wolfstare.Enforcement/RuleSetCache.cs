using Wolfstare.Core.Rules;

namespace Wolfstare.Enforcement;

/// <summary>
/// The rule set the network servers enforce, held in memory.
///
/// The DNS sinkhole evaluates rules on every lookup, so reading them must never touch the
/// database or take a lock. Readers see a single immutable <see cref="RuleSet"/> reference;
/// the enforcer swaps in a new one when sessions change. Reference assignment is atomic, so a
/// reader observes either the old set or the new one, never a mixture.
/// </summary>
public sealed class RuleSetCache
{
    private volatile RuleSet _current = RuleSet.Empty;

    public RuleSet Current => _current;

    public void Update(RuleSet rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        _current = rules;
    }
}
