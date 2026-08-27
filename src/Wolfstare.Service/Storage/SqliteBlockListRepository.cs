using Microsoft.Data.Sqlite;
using Wolfstare.Core.Rules;
using Wolfstare.Core.Storage;

namespace Wolfstare.Service.Storage;

public sealed class SqliteBlockListRepository(SqliteConnectionFactory factory) : IBlockListRepository
{
    public async Task<IReadOnlyList<BlockList>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, rules, allowlist FROM block_lists;";

        var results = new List<BlockList>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) results.Add(Read(reader));

        return results;
    }

    public async Task<BlockList?> GetAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, rules, allowlist FROM block_lists WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? Read(reader) : null;
    }

    public async Task SaveAsync(BlockList list, CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO block_lists (id, name, rules, allowlist)
            VALUES ($id, $name, $rules, $allowlist)
            ON CONFLICT(id) DO UPDATE SET
                name = excluded.name,
                rules = excluded.rules,
                allowlist = excluded.allowlist;
            """;

        command.Parameters.AddWithValue("$id", list.Id.ToString());
        command.Parameters.AddWithValue("$name", list.Name);
        command.Parameters.AddWithValue("$rules", RuleJson.Serialise(list.Rules));
        command.Parameters.AddWithValue("$allowlist", RuleJson.Serialise(list.Allowlist));

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM block_lists WHERE id = $id;";
        command.Parameters.AddWithValue("$id", id.ToString());

        await command.ExecuteNonQueryAsync(ct);
    }

    private static BlockList Read(SqliteDataReader reader)
        => new(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            RuleJson.Deserialise(reader.GetString(2)),
            RuleJson.Deserialise(reader.GetString(3)));
}
