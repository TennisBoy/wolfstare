using System.Globalization;
using Microsoft.Data.Sqlite;
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Storage;
using Wolfstare.Core.Time;

namespace Wolfstare.Service.Storage;

/// <summary>
/// Persistence for active sessions.
///
/// Timing values are stored as round-trippable ISO-8601 ("O") strings and integers rather
/// than as a serialised object: elapsed time is the only input to expiry, so a lossy round
/// trip would silently shorten or lengthen every lock across a restart.
/// </summary>
/// <param name="integrityKey">
/// Optional key that signs and verifies each session's integrity MAC (spec §6). When supplied,
/// a row whose MAC does not match is kept — dropping it would unblock, the wrong way to fail —
/// and <paramref name="onIntegrityFailure"/> is invoked so health can be marked degraded.
/// </param>
public sealed class SqliteSessionRepository(
    SqliteConnectionFactory factory,
    byte[]? integrityKey = null,
    Action<Guid>? onIntegrityFailure = null) : ISessionRepository
{
    private const string LockNone = "none";
    private const string LockPassword = "password";
    private const string LockTimed = "timed";
    private const string LockRandomText = "randomtext";

    public async Task<IReadOnlyList<BlockSession>> GetActiveAsync(CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, block_list_id, lock_kind, lock_hash, lock_salt, lock_iterations,
                   started_at_utc, elapsed_seconds, checkpoint_wall_utc,
                   checkpoint_monotonic_ms, duration_seconds, integrity_mac, lock_text
            FROM sessions;
            """;

        var results = new List<BlockSession>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var session = Read(reader);
            VerifyIntegrity(session, reader.IsDBNull(11) ? null : reader.GetString(11));
            results.Add(session);
        }

        return results;
    }

    private void VerifyIntegrity(BlockSession session, string? mac)
    {
        if (integrityKey is null) return;

        // A missing or non-matching MAC means the row was written without integrity (an older
        // record) or edited by hand. Either way, flag it — the session is kept, so the block
        // persists rather than failing open.
        if (mac is null || !SessionIntegrity.Verify(session, mac, integrityKey))
            onIntegrityFailure?.Invoke(session.Id);
    }

    public async Task SaveAsync(BlockSession session, CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO sessions (
                id, block_list_id, lock_kind, lock_hash, lock_salt, lock_iterations,
                started_at_utc, elapsed_seconds, checkpoint_wall_utc,
                checkpoint_monotonic_ms, duration_seconds, integrity_mac, lock_text)
            VALUES (
                $id, $blockListId, $lockKind, $lockHash, $lockSalt, $lockIterations,
                $startedAt, $elapsed, $checkpointWall, $checkpointMonotonic, $duration, $mac, $lockText)
            ON CONFLICT(id) DO UPDATE SET
                elapsed_seconds = excluded.elapsed_seconds,
                checkpoint_wall_utc = excluded.checkpoint_wall_utc,
                checkpoint_monotonic_ms = excluded.checkpoint_monotonic_ms,
                integrity_mac = excluded.integrity_mac;
            """;

        command.Parameters.AddWithValue("$id", session.Id.ToString());
        command.Parameters.AddWithValue("$blockListId", session.BlockListId.ToString());

        var (kind, hash, text) = Describe(session.Lock);
        command.Parameters.AddWithValue("$lockKind", kind);
        command.Parameters.AddWithValue("$lockHash", (object?)hash?.Hash ?? DBNull.Value);
        command.Parameters.AddWithValue("$lockSalt", (object?)hash?.Salt ?? DBNull.Value);
        command.Parameters.AddWithValue("$lockIterations", (object?)hash?.Iterations ?? DBNull.Value);
        command.Parameters.AddWithValue("$lockText", (object?)text ?? DBNull.Value);

        command.Parameters.AddWithValue("$startedAt", Format(session.Timing.StartedAtUtc));
        command.Parameters.AddWithValue("$elapsed", session.Timing.ElapsedSeconds);
        command.Parameters.AddWithValue("$checkpointWall", Format(session.Timing.CheckpointWallUtc));
        command.Parameters.AddWithValue("$checkpointMonotonic", session.Timing.CheckpointMonotonicMs);
        command.Parameters.AddWithValue("$duration", (object?)session.DurationSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$mac",
            integrityKey is null ? DBNull.Value : SessionIntegrity.Sign(session, integrityKey));

        await command.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var connection = factory.Open();
        await using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM sessions WHERE id = $id;";
        command.Parameters.AddWithValue("$id", sessionId.ToString());

        await command.ExecuteNonQueryAsync(ct);
    }

    private static (string Kind, PasswordHash? Hash, string? Text) Describe(SessionLock sessionLock) => sessionLock switch
    {
        NoLock => (LockNone, null, null),
        PasswordLock l => (LockPassword, l.Hash, null),
        TimedLock => (LockTimed, null, null),
        RandomTextLock l => (LockRandomText, null, l.RequiredText),
        _ => throw new InvalidDataException($"Cannot serialise lock type '{sessionLock.GetType().Name}'."),
    };

    private static BlockSession Read(SqliteDataReader reader)
    {
        var kind = reader.GetString(2);

        SessionLock sessionLock = kind switch
        {
            LockNone => new NoLock(),
            LockTimed => new TimedLock(),
            LockPassword => new PasswordLock(new PasswordHash(
                (byte[])reader.GetValue(3), (byte[])reader.GetValue(4), reader.GetInt32(5))),
            LockRandomText => new RandomTextLock(reader.IsDBNull(12) ? string.Empty : reader.GetString(12)),

            // Fail closed: an unreadable lock must not degrade into an unlocked session.
            _ => throw new InvalidDataException(
                $"Unknown lock kind '{kind}'. Refusing to load a session as unlocked."),
        };

        var timing = new SessionTiming(
            Parse(reader.GetString(6)),
            reader.GetInt64(7),
            Parse(reader.GetString(8)),
            reader.GetInt64(9));

        return new BlockSession(
            Guid.Parse(reader.GetString(0)),
            Guid.Parse(reader.GetString(1)),
            sessionLock,
            timing,
            reader.IsDBNull(10) ? null : reader.GetInt64(10));
    }

    private static string Format(DateTimeOffset value)
        => value.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset Parse(string value)
        => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}
