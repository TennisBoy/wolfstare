using Wolfstare.Core.Time;

namespace Wolfstare.Core.Tests.Time;

/// <summary>
/// The adversarial matrix for spec §5. A timed lock defeated by <c>Set-Date</c> is worthless,
/// so every one of these cases is a bypass attempt that must fail.
/// </summary>
public class ElapsedCalculatorTests
{
    private static (FakeClock Clock, SessionTiming Timing) StartSession()
    {
        var clock = new FakeClock();
        return (clock, SessionTiming.Start(clock));
    }

    [Fact]
    public void HonestPassageOfTimeAccrues()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(600, timing.ElapsedSeconds);
    }

    [Fact]
    public void RepeatedAdvancesAccumulateWithoutDoubleCounting()
    {
        var (clock, timing) = StartSession();

        for (var i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            timing = ElapsedCalculator.Advance(timing, clock);
        }

        Assert.Equal(300, timing.ElapsedSeconds);
    }

    [Fact]
    public void MovingClockForwardGrantsNoCredit()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(1));          // one honest minute
        clock.SetWallClock(clock.UtcNow.AddHours(10));   // user jumps the clock forward
        timing = ElapsedCalculator.Advance(timing, clock);

        // Monotonic only saw 60 seconds, so 60 seconds is all that counts.
        Assert.Equal(60, timing.ElapsedSeconds);
    }

    [Fact]
    public void MovingClockBackwardDoesNotLoseCredit()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);
        Assert.Equal(600, timing.ElapsedSeconds);

        clock.SetWallClock(clock.UtcNow.AddHours(-5));   // user rolls the clock back
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(600, timing.ElapsedSeconds);
    }

    [Fact]
    public void TimeContinuesAccruingNormallyAfterABackwardJump()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);

        clock.SetWallClock(clock.UtcNow.AddHours(-5));
        timing = ElapsedCalculator.Advance(timing, clock);

        clock.Advance(TimeSpan.FromMinutes(5));
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(900, timing.ElapsedSeconds);        // 10 + 5 minutes
    }

    [Fact]
    public void RepeatedForwardJumpsGrantNoCredit()
    {
        var (clock, timing) = StartSession();

        for (var i = 0; i < 20; i++)
        {
            clock.SetWallClock(clock.UtcNow.AddHours(1));
            timing = ElapsedCalculator.Advance(timing, clock);
        }

        Assert.Equal(0, timing.ElapsedSeconds);          // monotonic never moved
    }

    [Fact]
    public void RebootCreditsWallClockDowntime()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);

        clock.Reboot(TimeSpan.FromHours(8));
        timing = ElapsedCalculator.ResumeAfterRestart(timing, clock);

        // A genuine 8-hour shutdown should count: 600 + 28,800.
        Assert.Equal(29_400, timing.ElapsedSeconds);
    }

    [Fact]
    public void RebootWithClockRolledBackCreditsNothingButLosesNothing()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);

        clock.Reboot(TimeSpan.FromHours(-3));            // clock set back across the reboot
        timing = ElapsedCalculator.ResumeAfterRestart(timing, clock);

        Assert.Equal(600, timing.ElapsedSeconds);
    }

    [Fact]
    public void AccrualNeverGoesBackwardsUnderAnAdversarialClockSequence()
    {
        var (clock, timing) = StartSession();
        var previous = 0L;

        var jumps = new[] { 3600, -7200, 60, -60, 100_000, -100_000, 0 };
        foreach (var seconds in jumps)
        {
            clock.Advance(TimeSpan.FromSeconds(30));     // honest time between tamper attempts
            clock.SetWallClock(clock.UtcNow.AddSeconds(seconds));
            timing = ElapsedCalculator.Advance(timing, clock);

            Assert.True(timing.ElapsedSeconds >= previous,
                $"elapsed went backwards: {previous} -> {timing.ElapsedSeconds}");
            previous = timing.ElapsedSeconds;
        }

        // Seven honest 30-second intervals is the ceiling regardless of the jumps.
        Assert.Equal(210, timing.ElapsedSeconds);
    }

    [Fact]
    public void CheckpointAdvancesSoTheNextCallMeasuresFromTheNewPoint()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(1));
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(clock.UtcNow, timing.CheckpointWallUtc);
        Assert.Equal(clock.MonotonicMs, timing.CheckpointMonotonicMs);
    }

    [Fact]
    public void StartedAtIsPreservedAcrossAdvances()
    {
        var (clock, timing) = StartSession();
        var startedAt = timing.StartedAtUtc;

        clock.Advance(TimeSpan.FromHours(1));
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(startedAt, timing.StartedAtUtc);
    }

    [Fact]
    public void ResumeWithoutTamperingCreditsOnlyTheGap()
    {
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(5));
        timing = ElapsedCalculator.Advance(timing, clock);

        clock.Reboot(TimeSpan.FromMinutes(2));
        timing = ElapsedCalculator.ResumeAfterRestart(timing, clock);

        Assert.Equal(420, timing.ElapsedSeconds);        // 5 + 2 minutes
    }

    [Fact]
    public void AdvanceAfterResumeUsesTheFreshMonotonicBaseline()
    {
        // Regression guard: after a reboot the monotonic counter restarts near zero. If
        // Resume did not rebase the checkpoint, the next Advance would compute a large
        // negative monotonic delta and could corrupt the accrual.
        var (clock, timing) = StartSession();

        clock.Advance(TimeSpan.FromMinutes(10));
        timing = ElapsedCalculator.Advance(timing, clock);

        clock.Reboot(TimeSpan.FromHours(1));
        timing = ElapsedCalculator.ResumeAfterRestart(timing, clock);
        var afterResume = timing.ElapsedSeconds;

        clock.Advance(TimeSpan.FromMinutes(3));
        timing = ElapsedCalculator.Advance(timing, clock);

        Assert.Equal(afterResume + 180, timing.ElapsedSeconds);
    }
}
