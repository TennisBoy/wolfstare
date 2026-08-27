namespace Wolfstare.Core.Rules;

/// <summary>
/// A named set of rules the user starts a session against — Cold Turkey calls these "blocks".
/// </summary>
public sealed record BlockList(
    Guid Id,
    string Name,
    IReadOnlyList<BlockRule> Rules,
    IReadOnlyList<BlockRule> Allowlist)
{
    public RuleSet ToRuleSet() => new(Rules, Allowlist);

    /// <summary>
    /// Validates the list and every rule in it, returning the first failure. Rule failures are
    /// surfaced verbatim so the user sees which rule was wrong and why, not merely that the
    /// list was rejected.
    /// </summary>
    public static ValidationResult Validate(BlockList list)
    {
        if (string.IsNullOrWhiteSpace(list.Name))
            return ValidationResult.Fail("Block list name cannot be empty.");

        if (list.Rules.Count == 0)
            return ValidationResult.Fail(
                "A block list needs at least one rule, otherwise starting it would block nothing.");

        foreach (var rule in list.Rules)
        {
            var result = RuleValidator.Validate(rule);
            if (!result.IsValid) return result;
        }

        foreach (var rule in list.Allowlist)
        {
            var result = RuleValidator.Validate(rule);
            if (!result.IsValid) return result;
        }

        return ValidationResult.Ok;
    }
}
