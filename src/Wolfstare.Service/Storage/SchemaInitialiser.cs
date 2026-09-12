namespace Wolfstare.Service.Storage;

/// <summary>
/// Creates the database schema. Rules are stored as JSON rather than in relational tables:
/// the rule hierarchy is a closed sum type that is always read and written whole, so
/// normalising it would buy nothing and cost a join on every lookup.
/// </summary>
public static class SchemaInitialiser
{
    public static void Initialise(SqliteConnectionFactory factory)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();

        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS block_lists (
                id          TEXT PRIMARY KEY,
                name        TEXT NOT NULL,
                rules       TEXT NOT NULL,
                allowlist   TEXT NOT NULL
            );

            CREATE TABLE IF NOT EXISTS sessions (
                id                      TEXT PRIMARY KEY,
                block_list_id           TEXT NOT NULL,
                lock_kind               TEXT NOT NULL,
                lock_hash               BLOB,
                lock_salt               BLOB,
                lock_iterations         INTEGER,
                started_at_utc          TEXT NOT NULL,
                elapsed_seconds         INTEGER NOT NULL,
                checkpoint_wall_utc     TEXT NOT NULL,
                checkpoint_monotonic_ms INTEGER NOT NULL,
                duration_seconds        INTEGER,
                integrity_mac           TEXT
            );

            CREATE INDEX IF NOT EXISTS ix_sessions_block_list ON sessions (block_list_id);

            CREATE TABLE IF NOT EXISTS mutation_journal (
                key         TEXT PRIMARY KEY,
                original    TEXT
            );
            """;

        command.ExecuteNonQuery();
    }
}
