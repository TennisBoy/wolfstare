using Wolfstare.Core.Sessions;
using Wolfstare.Core.Tests.Time;

namespace Wolfstare.Core.Tests.Sessions;

/// <summary>
/// The cooldown after a wrong retype. Reporting the first mismatch is an oracle: a script could
/// guess one character at a time and watch the index advance. The cooldown makes that slower
/// than simply typing the text, and it runs on the monotonic clock so <c>Set-Date</c> can't skip
/// it.
/// </summary>
public class UnlockThrottleTests
{
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(5);
    private readonly FakeClock _clock = new();
    private readonly Guid _list = Guid.NewGuid();

    private UnlockThrottle Throttle() => new(_clock, Cooldown);

    [Fact]
    public void NothingIsThrottledBeforeAFailure()
        => Assert.Null(Throttle().Remaining(_list));

    [Fact]
    public void AFailureStartsTheFullCooldown()
    {
        var throttle = Throttle();

        throttle.RecordFailure(_list);

        Assert.Equal(Cooldown, throttle.Remaining(_list));
    }

    [Fact]
    public void TheCooldownCountsDownAndThenClears()
    {
        var throttle = Throttle();
        throttle.RecordFailure(_list);

        _clock.Advance(TimeSpan.FromSeconds(2));
        Assert.Equal(TimeSpan.FromSeconds(3), throttle.Remaining(_list));

        _clock.Advance(TimeSpan.FromSeconds(3));
        Assert.Null(throttle.Remaining(_list));
    }

    [Fact]
    public void MovingTheWallClockForwardDoesNotSkipTheCooldown()
    {
        var throttle = Throttle();
        throttle.RecordFailure(_list);

        _clock.SetWallClock(_clock.UtcNow.AddDays(1));

        Assert.Equal(Cooldown, throttle.Remaining(_list));
    }

    [Fact]
    public void ACooldownOnOneListDoesNotAffectAnother()
    {
        var throttle = Throttle();
        throttle.RecordFailure(_list);

        Assert.Null(throttle.Remaining(Guid.NewGuid()));
    }

    [Fact]
    public void AMonotonicCounterThatGoesBackwardsNeverLengthensTheWaitBeyondOneCooldown()
    {
        // MonotonicMs only resets on reboot, which also clears this in-memory state — but if it
        // ever did go backwards, the user must not be locked out for longer than one cooldown.
        var throttle = Throttle();
        _clock.Advance(TimeSpan.FromHours(1));
        throttle.RecordFailure(_list);

        _clock.Reboot(TimeSpan.Zero);

        Assert.Equal(Cooldown, throttle.Remaining(_list));
        _clock.Advance(Cooldown);
        Assert.Null(throttle.Remaining(_list));
    }
}
