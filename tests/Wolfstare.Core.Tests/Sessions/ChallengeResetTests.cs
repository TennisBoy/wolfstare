using Wolfstare.Core.Rules;
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Tests.Storage;
using Wolfstare.Core.Tests.Time;

namespace Wolfstare.Core.Tests.Sessions;

/// <summary>
/// Leaving the unlock page replaces the challenge with new words. The replacement must be
/// exactly as long as the original: if it could come out even one word shorter, leaving the tab
/// repeatedly would be a way to whittle the lock down.
/// </summary>
public class ChallengeResetTests
{
    private readonly FakeClock _clock = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly InMemoryBlockListRepository _lists = new();
    private readonly InMemorySessionRepository _sessions = new();

    private SessionManager NewManager() => new(_lists, _sessions, _hasher, _clock);

    private async Task<Guid> Started(SessionLock sessionLock)
    {
        var list = new BlockList(Guid.NewGuid(), "Focus", [new DomainRule("reddit.com")], []);
        await _lists.SaveAsync(list);
        await NewManager().StartAsync(new SessionStartRequest(list.Id, null, sessionLock));
        return list.Id;
    }

    [Theory]
    [InlineData(100)]
    [InlineData(101)]
    [InlineData(1496)]
    [InlineData(3000)]
    [InlineData(3885)]
    [InlineData(5000)]
    public void ExactGenerationHitsTheLengthPrecisely(int length)
    {
        var text = RandomText.GenerateExact(length);

        Assert.Equal(length, text.Length);
        Assert.All(text, c => Assert.True(c is (>= 'a' and <= 'z') or ' '));
        Assert.DoesNotContain("  ", text);
        Assert.False(text.StartsWith(' ') || text.EndsWith(' '));
    }

    [Fact]
    public void ExactGenerationIsExactForEveryLengthInTheAllowedRange()
    {
        for (var length = 100; length <= 5000; length += 7)
            Assert.Equal(length, RandomText.GenerateExact(length).Length);
    }

    [Fact]
    public async Task ResettingReplacesTheTextWithNewWordsOfTheSameLength()
    {
        var original = RandomText.Generate(3000);
        var id = await Started(new RandomTextLock(original));

        Assert.True(await NewManager().RegenerateChallengeAsync(id));

        var session = Assert.Single(await _sessions.GetActiveAsync());
        var replaced = Assert.IsType<RandomTextLock>(session.Lock).RequiredText;
        Assert.NotEqual(original, replaced);
        Assert.Equal(original.Length, replaced.Length);
    }

    [Fact]
    public async Task RepeatedResetsNeverShortenTheChallenge()
    {
        var original = RandomText.Generate(3000);
        var id = await Started(new RandomTextLock(original));
        var manager = NewManager();

        for (var i = 0; i < 50; i++) await manager.RegenerateChallengeAsync(id);

        var session = Assert.Single(await _sessions.GetActiveAsync());
        Assert.Equal(original.Length, Assert.IsType<RandomTextLock>(session.Lock).RequiredText.Length);
    }

    [Fact]
    public async Task AfterAResetOnlyTheNewTextUnlocks()
    {
        var original = RandomText.Generate(200);
        var id = await Started(new RandomTextLock(original));
        var manager = NewManager();
        await manager.RegenerateChallengeAsync(id);
        var replaced = ((RandomTextLock)(await _sessions.GetActiveAsync())[0].Lock).RequiredText;

        Assert.Equal(StopOutcome.PasswordIncorrect, await manager.StopAsync(id, original));
        Assert.Equal(StopOutcome.Allowed, await manager.StopAsync(id, replaced));
    }

    [Fact]
    public async Task AResetKeepsTheSessionsIdentityAndElapsedTime()
    {
        var id = await Started(new RandomTextLock(RandomText.Generate(200)));
        _clock.Advance(TimeSpan.FromMinutes(30));
        await NewManager().TickAsync();
        var before = Assert.Single(await _sessions.GetActiveAsync());

        await NewManager().RegenerateChallengeAsync(id);

        var after = Assert.Single(await _sessions.GetActiveAsync());
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(before.Timing, after.Timing);
        Assert.Equal(before.DurationSeconds, after.DurationSeconds);
    }

    [Fact]
    public async Task OnlyARandomTextLockCanBeReset()
    {
        var id = await Started(new NoLock());

        Assert.False(await NewManager().RegenerateChallengeAsync(id));
        Assert.IsType<NoLock>(Assert.Single(await _sessions.GetActiveAsync()).Lock);
    }

    [Fact]
    public async Task ResettingAListWithNoActiveSessionDoesNothing()
        => Assert.False(await NewManager().RegenerateChallengeAsync(Guid.NewGuid()));
}
