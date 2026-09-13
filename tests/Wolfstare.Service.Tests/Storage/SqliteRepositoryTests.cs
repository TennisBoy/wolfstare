using Wolfstare.Core.Rules;
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Time;
using Wolfstare.Service.Storage;

namespace Wolfstare.Service.Tests.Storage;

/// <summary>
/// Round-trip tests against a real temporary database. The closed rule and lock hierarchies
/// have to survive serialisation exactly, because a rule silently lost on reload would mean
/// a user believing they were blocked when they were not.
/// </summary>
public sealed class SqliteRepositoryTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"wolfstare-test-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _factory;

    public SqliteRepositoryTests()
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

    private SqliteBlockListRepository Lists => new(_factory);

    private SqliteSessionRepository Sessions => new(_factory);

    [Fact]
    public async Task BlockListRoundTripsEveryRuleKind()
    {
        var list = new BlockList(
            Guid.NewGuid(),
            "Deep work",
            [
                new DomainRule("*.reddit.com"),
                new AppRule(new ImageNameMatcher("4kvideodownloaderplus.exe")),
                new AppRule(new PublisherMatcher("Open Media LLC")),
                new AppRule(new FileDescriptionMatcher("4K Video Downloader")),
            ],
            [new DomainRule("github.com")]);

        await Lists.SaveAsync(list);
        var loaded = await Lists.GetAsync(list.Id);

        Assert.NotNull(loaded);
        Assert.Equal(list.Name, loaded.Name);
        Assert.Equal(list.Rules, loaded.Rules);
        Assert.Equal(list.Allowlist, loaded.Allowlist);
    }

    [Fact]
    public async Task SavingAnExistingBlockListReplacesIt()
    {
        var list = new BlockList(Guid.NewGuid(), "Focus", [new DomainRule("reddit.com")], []);
        await Lists.SaveAsync(list);

        await Lists.SaveAsync(list with { Name = "Renamed", Rules = [new DomainRule("youtube.com")] });

        var all = await Lists.GetAllAsync();
        var loaded = Assert.Single(all);
        Assert.Equal("Renamed", loaded.Name);
        Assert.Equal([new DomainRule("youtube.com")], loaded.Rules);
    }

    [Fact]
    public async Task DeletingABlockListRemovesIt()
    {
        var list = new BlockList(Guid.NewGuid(), "Focus", [new DomainRule("reddit.com")], []);
        await Lists.SaveAsync(list);

        await Lists.DeleteAsync(list.Id);

        Assert.Null(await Lists.GetAsync(list.Id));
        Assert.Empty(await Lists.GetAllAsync());
    }

    [Fact]
    public async Task MissingBlockListReadsAsNull()
        => Assert.Null(await Lists.GetAsync(Guid.NewGuid()));

    [Fact]
    public async Task SessionRoundTripsATimedLock()
    {
        var session = NewSession(new TimedLock(), 3600);

        await Sessions.SaveAsync(session);
        var loaded = Assert.Single(await Sessions.GetActiveAsync());

        Assert.Equal(session.Id, loaded.Id);
        Assert.Equal(session.BlockListId, loaded.BlockListId);
        Assert.IsType<TimedLock>(loaded.Lock);
        Assert.Equal(3600, loaded.DurationSeconds);
    }

    [Fact]
    public async Task SessionRoundTripsAPasswordLockIncludingHashBytes()
    {
        var hash = new Pbkdf2PasswordHasher().Create("hunter2");
        var session = NewSession(new PasswordLock(hash), null);

        await Sessions.SaveAsync(session);
        var loaded = Assert.Single(await Sessions.GetActiveAsync());

        var reloaded = Assert.IsType<PasswordLock>(loaded.Lock);
        Assert.Equal(hash.Hash, reloaded.Hash.Hash);
        Assert.Equal(hash.Salt, reloaded.Hash.Salt);
        Assert.Equal(hash.Iterations, reloaded.Hash.Iterations);
        Assert.True(new Pbkdf2PasswordHasher().Verify(reloaded.Hash, "hunter2"));
        Assert.Null(loaded.DurationSeconds);
    }

    [Fact]
    public async Task SessionRoundTripsARandomTextLock()
    {
        var text = RandomText.Generate(5000);
        await Sessions.SaveAsync(NewSession(new RandomTextLock(text), null));

        var loaded = Assert.Single(await Sessions.GetActiveAsync());
        var reloaded = Assert.IsType<RandomTextLock>(loaded.Lock);
        Assert.Equal(text, reloaded.RequiredText);
    }

    [Fact]
    public async Task SessionRoundTripsNoLock()
    {
        await Sessions.SaveAsync(NewSession(new NoLock(), 60));

        var loaded = Assert.Single(await Sessions.GetActiveAsync());
        Assert.IsType<NoLock>(loaded.Lock);
    }

    [Fact]
    public async Task SessionTimingSurvivesTheRoundTripExactly()
    {
        // Elapsed time is the only thing expiry is computed from, so a lossy round trip here
        // would silently shorten or lengthen every lock across a restart.
        var session = NewSession(new TimedLock(), 3600) with
        {
            Timing = new SessionTiming(
                new DateTimeOffset(2026, 3, 1, 9, 30, 0, TimeSpan.Zero),
                1234,
                new DateTimeOffset(2026, 3, 1, 9, 50, 34, TimeSpan.Zero),
                987_654),
        };

        await Sessions.SaveAsync(session);
        var loaded = Assert.Single(await Sessions.GetActiveAsync());

        Assert.Equal(session.Timing, loaded.Timing);
    }

    [Fact]
    public async Task RemovingASessionEndsIt()
    {
        var session = NewSession(new NoLock(), 60);
        await Sessions.SaveAsync(session);

        await Sessions.RemoveAsync(session.Id);

        Assert.Empty(await Sessions.GetActiveAsync());
    }

    [Fact]
    public async Task DataSurvivesANewConnection()
    {
        var list = new BlockList(Guid.NewGuid(), "Focus", [new DomainRule("reddit.com")], []);
        await Lists.SaveAsync(list);
        await Sessions.SaveAsync(NewSession(new TimedLock(), 3600) with { BlockListId = list.Id });

        // A fresh factory over the same file stands in for a service restart.
        var restarted = new SqliteConnectionFactory(_path);

        Assert.NotNull(await new SqliteBlockListRepository(restarted).GetAsync(list.Id));
        Assert.Single(await new SqliteSessionRepository(restarted).GetActiveAsync());
    }

    [Fact]
    public void UnknownRuleDiscriminatorThrowsRatherThanDroppingTheRule()
    {
        // Fail closed: a rule we cannot understand must not silently vanish from the list.
        Assert.Throws<InvalidDataException>(
            () => RuleJson.Deserialise("""[{"kind":"telepathy","value":"x"}]"""));
    }

    private static BlockSession NewSession(SessionLock lockSpec, long? duration)
        => new(Guid.NewGuid(), Guid.NewGuid(), lockSpec, SessionTiming.Start(new SystemClock()), duration);
}
