using Wolfstare.Core.Storage;

namespace Wolfstare.Service.Storage;

/// <summary>
/// Durable mutation journal (spec §7.5). A null <c>original</c> column is a real value — "the
/// setting was absent, remove it on restore" — so it is distinguished from the row not
/// existing, which means Wolfstare never touched that setting.
/// </summary>
public sealed class SqliteMutationJournal(SqliteConnectionFactory factory) : IMutationJournal
{
    public async Task<JournalEntry?> GetAsync(string key, CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT original FROM mutation_journal WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;

        return new JournalEntry(key, reader.IsDBNull(0) ? null : reader.GetString(0));
    }

    public async Task RecordAsync(JournalEntry entry, CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();

        // The first original recorded for a key wins: DO NOTHING on conflict preserves it if a
        // later apply tries to journal again.
        command.CommandText =
            """
            INSERT INTO mutation_journal (key, original)
            VALUES ($key, $original)
            ON CONFLICT(key) DO NOTHING;
            """;
        command.Parameters.AddWithValue("$key", entry.Key);
        command.Parameters.AddWithValue("$original", (object?)entry.Original ?? DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<JournalEntry>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, original FROM mutation_journal;";

        var results = new List<JournalEntry>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new JournalEntry(reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1)));

        return results;
    }

    public async Task ForgetAsync(string key, CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM mutation_journal WHERE key = $key;";
        command.Parameters.AddWithValue("$key", key);

        await command.ExecuteNonQueryAsync(ct);
    }
}
