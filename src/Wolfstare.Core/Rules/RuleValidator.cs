namespace Wolfstare.Core.Rules;

/// <summary>The outcome of validating a rule before it is persisted.</summary>
public readonly record struct ValidationResult(bool IsValid, string? Error)
{
    public static ValidationResult Ok { get; } = new(true, null);

    public static ValidationResult Fail(string error) => new(false, error);
}

/// <summary>
/// Validates rules at creation time. Every rejection carries a message the UI can show
/// verbatim, because a rule silently dropped at apply time is far worse than one refused
/// up front — the user would believe they were protected when they were not.
/// </summary>
public static class RuleValidator
{
    /// <summary>
    /// Publisher substrings broad enough to match large parts of the operating system or
    /// its drivers. A rule on "Microsoft" would have the ETW watcher terminating the
    /// machine out from under itself.
    /// </summary>
    private static readonly string[] ForbiddenPublisherSubstrings =
    [
        "microsoft", "windows", "nvidia", "intel", "advanced micro devices",
    ];

    public static ValidationResult Validate(BlockRule rule) => rule switch
    {
        DomainRule d => ValidateDomain(d),
        AppRule a => ValidateApp(a),
        PathRule => ValidationResult.Fail(
            "Path rules require HTTPS inspection, which is not enabled. Block the whole domain instead."),
        _ => ValidationResult.Fail($"Unknown rule type '{rule.GetType().Name}'."),
    };

    private static ValidationResult ValidateDomain(DomainRule rule)
    {
        var pattern = rule.Pattern?.Trim() ?? string.Empty;

        if (pattern.Length == 0)
            return ValidationResult.Fail("Domain pattern cannot be empty.");

        if (pattern == "*")
            return ValidationResult.Ok;

        if (pattern.Contains("://", StringComparison.Ordinal))
            return ValidationResult.Fail(
                $"'{pattern}' looks like a URL. Enter just the domain, for example 'reddit.com'.");

        if (pattern.Contains('/', StringComparison.Ordinal))
            return ValidationResult.Fail(
                $"'{pattern}' contains a path. Path rules require HTTPS inspection, which is not enabled.");

        var body = pattern.StartsWith("*.", StringComparison.Ordinal) ? pattern[2..] : pattern;

        if (body.Length == 0)
            return ValidationResult.Fail(
                "Wildcard pattern is missing a domain, for example '*.reddit.com'.");

        if (body.Contains('*', StringComparison.Ordinal))
            return ValidationResult.Fail(
                $"'{pattern}' is not supported. Use '*', 'example.com', or '*.example.com'.");

        if (!body.Contains('.', StringComparison.Ordinal))
            return ValidationResult.Fail(
                $"'{pattern}' is not a domain — it needs at least one dot, for example '{body}.com'.");

        foreach (var label in body.Split('.'))
        {
            if (label.Length == 0)
                return ValidationResult.Fail($"'{pattern}' has an empty label.");

            foreach (var c in label)
            {
                if (char.IsLetterOrDigit(c) || c == '-') continue;
                return ValidationResult.Fail($"'{pattern}' contains an invalid character '{c}'.");
            }
        }

        return ValidationResult.Ok;
    }

    private static ValidationResult ValidateApp(AppRule rule) => rule.Matcher switch
    {
        ImageNameMatcher m when string.IsNullOrWhiteSpace(m.ImageName)
            => ValidationResult.Fail("Application name cannot be empty."),

        ImageNameMatcher m when CriticalProcesses.IsProtected(m.ImageName)
            => ValidationResult.Fail(
                $"'{m.ImageName}' is a critical Windows process and cannot be blocked. "
                + "Blocking it would leave the machine unusable."),

        PublisherMatcher m when string.IsNullOrWhiteSpace(m.Publisher)
            => ValidationResult.Fail("Publisher cannot be empty."),

        PublisherMatcher m when IsForbiddenPublisher(m.Publisher)
            => ValidationResult.Fail(
                $"Publisher '{m.Publisher}' matches critical system software and cannot be blocked. "
                + "Target the specific application instead."),

        FileDescriptionMatcher m when string.IsNullOrWhiteSpace(m.Description)
            => ValidationResult.Fail("Description cannot be empty."),

        _ => ValidationResult.Ok,
    };

    private static bool IsForbiddenPublisher(string publisher)
    {
        foreach (var forbidden in ForbiddenPublisherSubstrings)
            if (publisher.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }
}
