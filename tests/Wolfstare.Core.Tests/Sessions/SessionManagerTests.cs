using Wolfstare.Core.Rules;
using Wolfstare.Core.Sessions;
using Wolfstare.Core.Tests.Storage;
using Wolfstare.Core.Tests.Time;

namespace Wolfstare.Core.Tests.Sessions;

public class SessionManagerTests
{
    private readonly FakeClock _clock = new();
    private readonly Pbkdf2PasswordHasher _hasher = new();
    private readonly InMemoryBlockListRepository _lists = new();
    private readonly InMemorySessionRepository _sessions = new();

    private SessionManager NewManager() => new(_lists, _sessions, _hasher, _clock);

    private async Task<BlockList> GivenBlockList(
        BlockRule[]? rules = null, BlockRule[]? allowlist = null)
    {
        var list = new BlockList(
            Guid.NewGuid(), "Focus",
            rules ?? [new DomainRule("reddit.com")],
            allowlist ?? []);

        await _lists.SaveAsync(list);
        return list;
    }

    [Fact]
    public async Task StartingASessionForAnUnknownListFails()
    {
        var result = await NewManager().StartAsync(
            new SessionStartRequest(Guid.NewGuid(), 3600, new NoLock()));

        Assert.Equal(StartFailure.BlockListNotFound, result.Failure);
        Assert.Null(result.Session);
    }

    [Fact]
    public async Task StartingASessionSucceedsAndItBecomesActive()
    {
        var list = await GivenBlockList();
        var manager = NewManager();

        var result = await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));

        Assert.Equal(StartFailure.None, result.Failure);
        Assert.NotNull(result.Session);
        Assert.Single(await manager.GetActiveAsync());
    }

    [Fact]
    public async Task StartingASecondSessionForTheSameListFails()
    {
        var list = await GivenBlockList();
        var manager = NewManager();
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));

        var result = await manager.StartAsync(new SessionStartRequest(list.Id, 60, new NoLock()));

        Assert.Equal(StartFailure.AlreadyActive, result.Failure);
    }

    [Fact]
    public async Task TimedLockWithoutADurationIsRefused()
    {
        // An indefinite timed lock could never be stopped by anything. That is a footgun,
        // not a feature.
        var list = await GivenBlockList();

        var result = await NewManager().StartAsync(
            new SessionStartRequest(list.Id, null, new TimedLock()));

        Assert.Equal(StartFailure.TimedLockNeedsDuration, result.Failure);
    }

    [Fact]
    public async Task TickAccruesTimeAndPersistsTheCheckpoint()
    {
        var list = await GivenBlockList();
        var manager = NewManager();
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new TimedLock()));

        _clock.Advance(TimeSpan.FromMinutes(5));
        await manager.TickAsync();

        var active = Assert.Single(await manager.GetActiveAsync());
        Assert.Equal(300, active.Timing.ElapsedSeconds);
        Assert.Equal(3300, active.RemainingSeconds());
    }

    [Fact]
    public async Task TickEndsAnExpiredSession()
    {
        var list = await GivenBlockList();
        var manager = NewManager();
        await manager.StartAsync(new SessionStartRequest(list.Id, 60, new TimedLock()));

        _clock.Advance(TimeSpan.FromSeconds(61));
        await manager.TickAsync();

        Assert.Empty(await manager.GetActiveAsync());
    }

    [Fact]
    public async Task StoppingATimedSessionIsRefusedAndLeavesItActive()
    {
        var list = await GivenBlockList();
        var manager = NewManager();
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new TimedLock()));

        var outcome = await manager.StopAsync(list.Id, password: null);

        Assert.Equal(StopOutcome.Locked, outcome);
        Assert.Single(await manager.GetActiveAsync());
    }

    [Fact]
    public async Task StoppingATimedSessionWithAPasswordIsStillRefused()
    {
        var list = await GivenBlockList();
        var manager = NewManager();
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new TimedLock()));

        Assert.Equal(StopOutcome.Locked, await manager.StopAsync(list.Id, "please"));
        Assert.Single(await manager.GetActiveAsync());
    }

    [Fact]
    public async Task StoppingAPasswordSessionWithTheWrongPasswordLeavesItActive()
    {
        var list = await GivenBlockList();
        var manager = NewManager();
        await manager.StartAsync(
            new SessionStartRequest(list.Id, null, new PasswordLock(_hasher.Create("hunter2"))));

        var outcome = await manager.StopAsync(list.Id, "wrong");

        Assert.Equal(StopOutcome.PasswordIncorrect, outcome);
        Assert.Single(await manager.GetActiveAsync());
    }

    [Fact]
    public async Task StoppingAPasswordSessionWithTheCorrectPasswordEndsIt()
    {
        var list = await GivenBlockList();
        var manager = NewManager();
        await manager.StartAsync(
            new SessionStartRequest(list.Id, null, new PasswordLock(_hasher.Create("hunter2"))));

        var outcome = await manager.StopAsync(list.Id, "hunter2");

        Assert.Equal(StopOutcome.Allowed, outcome);
        Assert.Empty(await manager.GetActiveAsync());
    }

    [Fact]
    public async Task StoppingAnUnlockedSessionEndsIt()
    {
        var list = await GivenBlockList();
        var manager = NewManager();
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));

        Assert.Equal(StopOutcome.Allowed, await manager.StopAsync(list.Id, null));
        Assert.Empty(await manager.GetActiveAsync());
    }

    [Fact]
    public async Task EffectiveRuleSetIsEmptyWithNoActiveSessions()
    {
        var set = await NewManager().GetEffectiveRuleSetAsync();

        Assert.Equal(Decision.Permit, set.EvaluateDomain("reddit.com"));
    }

    [Fact]
    public async Task EffectiveRuleSetUnionsAllActiveSessions()
    {
        var social = await GivenBlockList(rules: [new DomainRule("reddit.com")]);
        var video = await GivenBlockList(rules: [new DomainRule("youtube.com")]);
        var manager = NewManager();

        await manager.StartAsync(new SessionStartRequest(social.Id, 3600, new NoLock()));
        await manager.StartAsync(new SessionStartRequest(video.Id, 3600, new NoLock()));

        var set = await manager.GetEffectiveRuleSetAsync();

        Assert.Equal(Decision.Deny, set.EvaluateDomain("reddit.com"));
        Assert.Equal(Decision.Deny, set.EvaluateDomain("youtube.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("example.com"));
    }

    [Fact]
    public async Task EffectiveRuleSetUnionsAllowlists()
    {
        var list = await GivenBlockList(
            rules: [new DomainRule("*")],
            allowlist: [new DomainRule("github.com")]);
        var manager = NewManager();
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));

        var set = await manager.GetEffectiveRuleSetAsync();

        Assert.Equal(Decision.Deny, set.EvaluateDomain("reddit.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("github.com"));
    }

    [Fact]
    public async Task SessionWhoseBlockListWasDeletedContributesNothingAndDoesNotThrow()
    {
        var list = await GivenBlockList();
        var manager = NewManager();
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));

        await _lists.DeleteAsync(list.Id);

        var set = await manager.GetEffectiveRuleSetAsync();
        Assert.Equal(Decision.Permit, set.EvaluateDomain("reddit.com"));
    }

    [Fact]
    public async Task ResumeAccruesDowntimeAcrossARestart()
    {
        var list = await GivenBlockList();
        var first = NewManager();
        await first.StartAsync(new SessionStartRequest(list.Id, 7200, new TimedLock()));

        _clock.Advance(TimeSpan.FromMinutes(10));
        await first.TickAsync();

        // A new manager over the same repositories stands in for a service restart.
        _clock.Reboot(TimeSpan.FromMinutes(30));
        var second = NewManager();
        await second.ResumeAsync();

        var active = Assert.Single(await second.GetActiveAsync());
        Assert.Equal(2400, active.Timing.ElapsedSeconds);   // 10 + 30 minutes
    }

    [Fact]
    public async Task ResumeEndsASessionThatExpiredWhileTheServiceWasDown()
    {
        var list = await GivenBlockList();
        var first = NewManager();
        await first.StartAsync(new SessionStartRequest(list.Id, 600, new TimedLock()));

        _clock.Reboot(TimeSpan.FromHours(4));
        var second = NewManager();
        await second.ResumeAsync();

        Assert.Empty(await second.GetActiveAsync());
    }

    [Fact]
    public async Task StoppingAListWithNoActiveSessionIsAllowed()
    {
        var list = await GivenBlockList();

        Assert.Equal(StopOutcome.Allowed, await NewManager().StopAsync(list.Id, null));
    }
}
