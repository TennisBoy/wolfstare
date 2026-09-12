namespace Wolfstare.Core.Enforcement;

/// <summary>
/// One reversible piece of machine configuration — an interface's DNS servers, a firewall
/// rule, a browser policy key. Implemented in the enforcement layer; modelled here so the
/// mutator's logic is testable without touching the real machine.
///
/// <see cref="ReadAsync"/> returns null when the setting is absent (a policy key that does not
/// exist), which is distinct from a present-but-empty value. <see cref="WriteAsync"/> with null
/// removes the setting.
/// </summary>
public interface ISystemSetting
{
    /// <summary>Stable identity used as the journal key. Must be unique across all settings.</summary>
    string Key { get; }

    Task<string?> ReadAsync(CancellationToken ct);

    Task WriteAsync(string? value, CancellationToken ct);
}

/// <summary>
/// Applies reversible changes to machine configuration, recording each original so it can be
/// put back — on demand, or at the next startup after a crash (spec §7.5).
/// </summary>
public interface ISystemMutator
{
    /// <summary>
    /// Records the setting's current value (once, on first touch) then writes the desired one.
    /// A no-op when the setting already holds the desired value.
    /// </summary>
    Task ApplyAsync(ISystemSetting setting, string? desired, CancellationToken ct);

    /// <summary>Writes every journalled original back and forgets the ones that restored.</summary>
    Task<RestoreReport> RestoreAllAsync(CancellationToken ct);

    /// <summary>
    /// Restores only the journalled settings whose key matches <paramref name="keyFilter"/>.
    ///
    /// Website and app enforcement share one journal but engage independently — a session may
    /// block only apps, or only sites — so each must be able to put its own settings back
    /// without disturbing the other's. A full restore is this with a match-everything filter.
    /// </summary>
    Task<RestoreReport> RestoreMatchingAsync(Func<string, bool> keyFilter, CancellationToken ct);
}

/// <summary>The outcome of a restore pass. Entries that did not restore are kept for retry.</summary>
public sealed record RestoreReport(
    IReadOnlyList<string> Restored,
    IReadOnlyList<(string Key, Exception Error)> Failed,
    IReadOnlyList<string> Unresolved)
{
    /// <summary>True when nothing was left behind — every journalled original is back in place.</summary>
    public bool IsComplete => Failed.Count == 0 && Unresolved.Count == 0;
}
