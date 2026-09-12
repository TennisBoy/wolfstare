namespace Wolfstare.Core.Storage;

/// <summary>The original value of one system setting, captured before Wolfstare changed it.</summary>
/// <param name="Original">
/// The value to restore. Null means the setting was absent and should be removed on restore —
/// which is why an entry existing at all matters, not just its value.
/// </param>
public sealed record JournalEntry(string Key, string? Original);

/// <summary>
/// Durable record of the machine changes Wolfstare has made, so they can be undone even after
/// a crash that loses all in-memory state. Only the original is stored, and only the first
/// time a key is touched — later applies must not overwrite it with a Wolfstare-set value.
/// </summary>
public interface IMutationJournal
{
    Task<JournalEntry?> GetAsync(string key, CancellationToken ct = default);

    /// <summary>Records an entry if the key is not already journalled; otherwise a no-op.</summary>
    Task RecordAsync(JournalEntry entry, CancellationToken ct = default);

    Task<IReadOnlyList<JournalEntry>> GetAllAsync(CancellationToken ct = default);

    Task ForgetAsync(string key, CancellationToken ct = default);
}
