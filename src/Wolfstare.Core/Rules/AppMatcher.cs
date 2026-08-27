namespace Wolfstare.Core.Rules;

/// <summary>How an <see cref="AppRule"/> recognises an executable.</summary>
public abstract record AppMatcher
{
    public abstract bool Matches(ProcessIdentity identity);
}

/// <summary>
/// Matches on filename alone, never on install path — which is what lets a rule be written
/// for an application that is not installed yet.
/// </summary>
public sealed record ImageNameMatcher(string ImageName) : AppMatcher
{
    public override bool Matches(ProcessIdentity identity)
    {
        var wanted = Canonical(ImageName);
        var actual = Canonical(identity.ImageName);
        return wanted.Length != 0 && wanted.Equals(actual, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Strips any directory and normalises to a lowercase name ending in ".exe".</summary>
    internal static string Canonical(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var name = value.Trim().Replace('/', '\\');
        var slash = name.LastIndexOf('\\');
        if (slash >= 0) name = name[(slash + 1)..];

        name = name.ToLowerInvariant();
        return name.EndsWith(".exe", StringComparison.Ordinal) ? name : name + ".exe";
    }
}

/// <summary>
/// Matches the Authenticode certificate subject as a case-insensitive substring. This is the
/// matcher that survives a rename, since renaming a file does not change who signed it.
/// </summary>
public sealed record PublisherMatcher(string Publisher) : AppMatcher
{
    public override bool Matches(ProcessIdentity identity)
        => !string.IsNullOrWhiteSpace(Publisher)
           && identity.Publisher is { } actual
           && actual.Contains(Publisher, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Matches the Win32 version-info FileDescription as a case-insensitive substring.</summary>
public sealed record FileDescriptionMatcher(string Description) : AppMatcher
{
    public override bool Matches(ProcessIdentity identity)
        => !string.IsNullOrWhiteSpace(Description)
           && identity.FileDescription is { } actual
           && actual.Contains(Description, StringComparison.OrdinalIgnoreCase);
}
