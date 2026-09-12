using Wolfstare.Core.Storage;

namespace Wolfstare.Core.Enforcement;

/// <summary>
/// Applies system settings through a journal so every change can be undone (spec §7.5).
///
/// The ordering is the whole point: the original is journalled <em>before</em> the write. A
/// crash between the two is safe — restore writes the journalled original back, which is a
/// harmless no-op if the write never landed. A crash after the write leaves the journal
/// correct. There is no ordering that loses the original.
///
/// This is pure orchestration over Core interfaces, so it lives in Core and is unit-tested
/// with fake settings. The Windows-specific settings it drives live in the enforcement layer.
/// It carries no logger — every abnormal outcome is surfaced through <see cref="RestoreReport"/>,
/// which the caller (in the Windows layer, where logging is available) inspects and logs.
/// </summary>
public sealed class JournalledMutator(
    IMutationJournal journal,
    Func<string, ISystemSetting?> resolve) : ISystemMutator
{
    public async Task ApplyAsync(ISystemSetting setting, string? desired, CancellationToken ct)
    {
        var current = await setting.ReadAsync(ct);

        // Journal only on first touch. Re-journalling would capture our own value as the
        // "original" and make the real one unrecoverable.
        if (await journal.GetAsync(setting.Key, ct) is null)
            await journal.RecordAsync(new JournalEntry(setting.Key, current), ct);

        // The enforcer re-applies on every refresh; skip the write when nothing would change so
        // it is not shelling out to netsh every couple of seconds.
        if (current == desired) return;

        await setting.WriteAsync(desired, ct);
    }

    public async Task<RestoreReport> RestoreAllAsync(CancellationToken ct)
    {
        var restored = new List<string>();
        var failed = new List<(string, Exception)>();
        var unresolved = new List<string>();

        foreach (var entry in await journal.GetAllAsync(ct))
        {
            var setting = resolve(entry.Key);
            if (setting is null)
            {
                // The setting cannot be resolved right now — perhaps the network adapter was
                // removed while the service was down. Keep the entry so a later run, when the
                // adapter is back, can still restore it.
                unresolved.Add(entry.Key);
                continue;
            }

            try
            {
                await setting.WriteAsync(entry.Original, ct);
                await journal.ForgetAsync(entry.Key, ct);
                restored.Add(entry.Key);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Keep the entry and report it. A failed restore that dropped its journal entry
                // would strand the machine with a Wolfstare-set value and no record of the real one.
                failed.Add((entry.Key, ex));
            }
        }

        return new RestoreReport(restored, failed, unresolved);
    }
}
