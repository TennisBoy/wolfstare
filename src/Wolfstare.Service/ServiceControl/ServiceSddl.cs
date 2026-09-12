namespace Wolfstare.Service.ServiceControl;

/// <summary>
/// Transforms a Windows service security descriptor (SDDL) to deny, or re-allow, the stop right
/// for the interactive user (spec §9). Denying stop while a lock is active is what stops a timed
/// block being ended with `sc stop` or the Services console.
///
/// Pure string manipulation, so it is unit-tested; applying the result with `sc sdset` is a
/// scripted, privileged step. A deny ACE only takes effect ahead of a matching allow, so the
/// denies are inserted at the front of the DACL.
/// </summary>
public static class ServiceSddl
{
    // SERVICE_STOP is "WP" in the service-specific access bits. IU = interactive users,
    // SU = service logon users; both can otherwise stop a service they were granted control of.
    private static readonly string[] DenyAces = ["(D;;WP;;;IU)", "(D;;WP;;;SU)"];

    private const string DaclPrefix = "D:";

    public static string DenyStop(string sddl)
    {
        var (flags, aces) = SplitDacl(sddl);
        if (aces is null) return sddl; // no DACL to modify

        var toAdd = DenyAces.Where(ace => !aces.Contains(ace, StringComparison.Ordinal));
        return $"{DaclPrefix}{flags}{string.Concat(toAdd)}{aces}";
    }

    public static string AllowStop(string sddl)
    {
        var (flags, aces) = SplitDacl(sddl);
        if (aces is null) return sddl;

        foreach (var deny in DenyAces)
            aces = aces.Replace(deny, string.Empty, StringComparison.Ordinal);

        return $"{DaclPrefix}{flags}{aces}";
    }

    /// <summary>Splits a DACL into its flag characters (before the first ACE) and its ACE string.</summary>
    private static (string Flags, string? Aces) SplitDacl(string sddl)
    {
        var daclStart = sddl.IndexOf(DaclPrefix, StringComparison.Ordinal);
        if (daclStart < 0) return (string.Empty, null);

        var body = sddl[(daclStart + DaclPrefix.Length)..];
        var firstAce = body.IndexOf('(');
        if (firstAce < 0) return (body, string.Empty);

        return (body[..firstAce], body[firstAce..]);
    }
}
