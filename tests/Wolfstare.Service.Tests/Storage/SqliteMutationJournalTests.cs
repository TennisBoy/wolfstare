using Wolfstare.Core.Storage;
using Wolfstare.Service.Storage;

namespace Wolfstare.Service.Tests.Storage;

public sealed class SqliteMutationJournalTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wolfstare-journal-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _factory;

    public SqliteMutationJournalTests()
    {
        _factory = new SqliteConnectionFactory(_path);
        SchemaInitialiser.Initialise(_factory);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(_path + suffix); } catch (IOException) { /* best effort */ }
        }
    }

    private SqliteMutationJournal Journal => new(_factory);

    [Fact]
    public async Task RecordedEntryRoundTrips()
    {
        await Journal.RecordAsync(new JournalEntry("dns:ipv4:Wi-Fi", "192.168.1.1,1.1.1.1"));

        Assert.Equal(
            new JournalEntry("dns:ipv4:Wi-Fi", "192.168.1.1,1.1.1.1"),
            await Journal.GetAsync("dns:ipv4:Wi-Fi"));
    }

    [Fact]
    public async Task NullOriginalIsDistinctFromNoEntry()
    {
        // "The setting did not exist" and "we never touched it" restore very differently.
        await Journal.RecordAsync(new JournalEntry("registry:chrome-doh", null));

        var entry = await Journal.GetAsync("registry:chrome-doh");

        Assert.NotNull(entry);
        Assert.Null(entry.Original);
        Assert.Null(await Journal.GetAsync("never-recorded"));
    }

    [Fact]
    public async Task FirstRecordedOriginalWins()
    {
        await Journal.RecordAsync(new JournalEntry("dns:ipv4:Wi-Fi", "192.168.1.1"));
        await Journal.RecordAsync(new JournalEntry("dns:ipv4:Wi-Fi", "127.0.0.1"));

        Assert.Equal("192.168.1.1", (await Journal.GetAsync("dns:ipv4:Wi-Fi"))!.Original);
    }

    [Fact]
    public async Task GetAllReturnsEveryEntry()
    {
        await Journal.RecordAsync(new JournalEntry("a", "1"));
        await Journal.RecordAsync(new JournalEntry("b", null));

        var all = await Journal.GetAllAsync();

        Assert.Equal(2, all.Count);
        Assert.Contains(new JournalEntry("a", "1"), all);
        Assert.Contains(new JournalEntry("b", null), all);
    }

    [Fact]
    public async Task ForgetRemovesTheEntry()
    {
        await Journal.RecordAsync(new JournalEntry("a", "1"));

        await Journal.ForgetAsync("a");

        Assert.Null(await Journal.GetAsync("a"));
        Assert.Empty(await Journal.GetAllAsync());
    }

    [Fact]
    public async Task EntriesSurviveARestart()
    {
        await Journal.RecordAsync(new JournalEntry("dns:ipv4:Wi-Fi", "192.168.1.1"));

        var restarted = new SqliteMutationJournal(new SqliteConnectionFactory(_path));

        Assert.NotNull(await restarted.GetAsync("dns:ipv4:Wi-Fi"));
    }
}
