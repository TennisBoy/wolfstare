using Wolfstare.Core.Sessions;
using Wolfstare.Core.Tests.Time;
using Wolfstare.Core.Time;

namespace Wolfstare.Core.Tests.Sessions;

public class StopPolicyTests
{
    private readonly Pbkdf2PasswordHasher _hasher = new();

    private static BlockSession Session(SessionLock lockSpec, IClock clock, long? durationSeconds)
        => new(Guid.NewGuid(), Guid.NewGuid(), lockSpec, SessionTiming.Start(clock), durationSeconds);

    [Fact]
    public void UnlockedSessionCanBeStoppedFreely()
    {
        var clock = new FakeClock();
        var session = Session(new NoLock(), clock, durationSeconds: 3600);

        Assert.Equal(StopOutcome.Allowed, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void TimedLockCannotBeStoppedEarly()
    {
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: 3600);

        Assert.Equal(StopOutcome.Locked, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void TimedLockDoesNotYieldToAPassword()
    {
        // The whole point of a timed lock: no credential ends it. Not even a correct one.
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: 3600);

        Assert.Equal(StopOutcome.Locked, StopPolicy.CanStop(session, "any password", _hasher, clock));
    }

    [Fact]
    public void TimedLockCanBeStoppedOnceExpired()
    {
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: 60);

        clock.Advance(TimeSpan.FromSeconds(61));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };

        Assert.Equal(StopOutcome.Allowed, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void TimedLockSurvivesAForwardClockJump()
    {
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: 3600);

        clock.SetWallClock(clock.UtcNow.AddDays(1));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };

        Assert.Equal(StopOutcome.Locked, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void PasswordLockRequiresAPassword()
    {
        var clock = new FakeClock();
        var session = Session(new PasswordLock(_hasher.Create("hunter2")), clock, null);

        Assert.Equal(StopOutcome.PasswordRequired, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void PasswordLockRejectsTheWrongPassword()
    {
        var clock = new FakeClock();
        var session = Session(new PasswordLock(_hasher.Create("hunter2")), clock, null);

        Assert.Equal(StopOutcome.PasswordIncorrect, StopPolicy.CanStop(session, "hunter3", _hasher, clock));
    }

    [Fact]
    public void PasswordLockAcceptsTheCorrectPassword()
    {
        var clock = new FakeClock();
        var session = Session(new PasswordLock(_hasher.Create("hunter2")), clock, null);

        Assert.Equal(StopOutcome.Allowed, StopPolicy.CanStop(session, "hunter2", _hasher, clock));
    }

    [Fact]
    public void IndefiniteSessionNeverExpires()
    {
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: null);

        clock.Advance(TimeSpan.FromDays(365));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };

        Assert.False(session.IsExpired());
        Assert.Null(session.RemainingSeconds());
        Assert.Equal(StopOutcome.Locked, StopPolicy.CanStop(session, null, _hasher, clock));
    }

    [Fact]
    public void RemainingSecondsCountsDownAndFloorsAtZero()
    {
        var clock = new FakeClock();
        var session = Session(new TimedLock(), clock, durationSeconds: 100);

        Assert.Equal(100, session.RemainingSeconds());

        clock.Advance(TimeSpan.FromSeconds(40));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };
        Assert.Equal(60, session.RemainingSeconds());

        clock.Advance(TimeSpan.FromSeconds(500));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };
        Assert.Equal(0, session.RemainingSeconds());
    }

    [Fact]
    public void CorruptPasswordLockFailsClosed()
    {
        // An unreadable verifier must leave the session locked, never open it.
        var clock = new FakeClock();
        var session = Session(new PasswordLock(new PasswordHash([], [], 0)), clock, null);

        Assert.Equal(StopOutcome.PasswordIncorrect, StopPolicy.CanStop(session, "anything", _hasher, clock));
    }

    [Fact]
    public void ExpiredPasswordLockedSessionNoLongerNeedsThePassword()
    {
        var clock = new FakeClock();
        var session = Session(new PasswordLock(_hasher.Create("hunter2")), clock, durationSeconds: 60);

        clock.Advance(TimeSpan.FromSeconds(61));
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };

        Assert.Equal(StopOutcome.Allowed, StopPolicy.CanStop(session, null, _hasher, clock));
    }
}
