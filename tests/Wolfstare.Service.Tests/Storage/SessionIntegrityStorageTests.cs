using System.Security.Cryptography;
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Time;
using Wolfstare.Service.Storage;

namespace Wolfstare.Service.Tests.Storage;

/// <summary>
/// The session repository, given an integrity key, signs each row and flags one whose MAC does
/// not match on load — without dropping it, so a tampered database keeps its blocks (spec §6).
/// </summary>
public sealed class SessionIntegrityStorageTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wolfstare-integ-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _factory;
    private readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public SessionIntegrityStorageTests()
    {
        _factory = new SqliteConnectionFactory(_path);
        SchemaInitialiser.Initialise(_factory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var s in new[] { "", "-wal", "-shm" })
            try { File.Delete(_path + s); } catch (IOException) { }
    }

    private BlockSession Session(long? duration = 3600)
        => new(Guid.NewGuid(), Guid.NewGuid(), new TimedLock(), SessionTiming.Start(new SystemClock()), duration);

    [Fact]
    public async Task AnUntamperedSessionLoadsWithoutFlagging()
    {
        var flagged = new List<Guid>();
        var repo = new SqliteSessionRepository(_factory, _key, flagged.Add);
        await repo.SaveAsync(Session());

        var loaded = await repo.GetActiveAsync();

        Assert.Single(loaded);
        Assert.Empty(flagged);
    }

    [Fact]
    public async Task AHandEditedDurationIsFlaggedButTheSessionIsKept()
    {
        var session = Session(duration: 3600);
        await new SqliteSessionRepository(_factory, _key).SaveAsync(session);

        // Simulate a text-editor edit: shorten the duration directly in the database, leaving
        // the old MAC in place.
        await using (var connection = _factory.Open())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "UPDATE sessions SET duration_seconds = 1 WHERE id = $id;";
            command.Parameters.AddWithValue("$id", session.Id.ToString());
            await command.ExecuteNonQueryAsync();
        }

        var flagged = new List<Guid>();
        var repo = new SqliteSessionRepository(_factory, _key, flagged.Add);
        var loaded = await repo.GetActiveAsync();

        Assert.Single(loaded);                       // kept — blocks persist, fail closed
        Assert.Contains(session.Id, flagged);        // and the tamper is reported
    }

    [Fact]
    public async Task ARowWrittenWithoutIntegrityIsFlaggedOnLoadUnderAKey()
    {
        // A session saved by a build that had no key (null mac) must not silently pass a later
        // integrity-checking load.
        await new SqliteSessionRepository(_factory).SaveAsync(Session());

        var flagged = new List<Guid>();
        await new SqliteSessionRepository(_factory, _key, flagged.Add).GetActiveAsync();

        Assert.Single(flagged);
    }

    [Fact]
    public async Task WithoutAKeyNothingIsChecked()
    {
        var repo = new SqliteSessionRepository(_factory);
        await repo.SaveAsync(Session());

        Assert.Single(await repo.GetActiveAsync());  // no key, no verification, no throw
    }
}
