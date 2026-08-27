namespace Wolfstare.Core.Time;

/// <summary>
/// Accrues elapsed time for an active session in a way that resists clock tampering
/// (spec §5).
///
/// The rule: a tick may credit no more than the monotonic counter actually observed, and
/// never a negative amount. Since the monotonic counter cannot be set by the user, that
/// ceiling is what makes the whole scheme work — the most a session can ever accrue is the
/// time the machine has genuinely been running.
///
/// Moving the system clock forward inflates the wall delta but not the monotonic one, so
/// taking the minimum discards the fabricated time. Moving it backward is handled separately
/// rather than by clamping the minimum to zero: a negative wall delta is itself the evidence
/// that the wall clock is lying, and forfeiting the tick would discard honest time the
/// monotonic counter had already earned. That would not be a bypass — losing credit only
/// makes a block last longer — but it would penalise an ordinary backward NTP correction, so
/// we fall back to the reading we trust instead.
/// </summary>
public static class ElapsedCalculator
{
    /// <summary>
    /// Accrues time since the last checkpoint. Safe to call at any frequency — the amount
    /// credited depends on the clocks, not on how often this is called.
    /// </summary>
    public static SessionTiming Advance(SessionTiming timing, IClock clock)
    {
        var nowWall = clock.UtcNow;
        var nowMonotonic = clock.MonotonicMs;

        var wallDelta = (long)(nowWall - timing.CheckpointWallUtc).TotalSeconds;
        var monotonicDelta = (nowMonotonic - timing.CheckpointMonotonicMs) / 1000;

        // A negative monotonic delta means the counter reset (a reboot that did not go
        // through ResumeAfterRestart). Credit nothing rather than guessing.
        if (monotonicDelta < 0) monotonicDelta = 0;

        // A negative wall delta means the clock was moved back. Monotonic is still honest,
        // so trust it outright rather than forfeiting the tick.
        var advance = wallDelta < 0 ? monotonicDelta : Math.Min(wallDelta, monotonicDelta);

        return timing with
        {
            ElapsedSeconds = timing.ElapsedSeconds + advance,
            CheckpointWallUtc = nowWall,
            CheckpointMonotonicMs = nowMonotonic,
        };
    }

    /// <summary>
    /// Resumes a session after a service restart or reboot.
    ///
    /// The monotonic counter resets at boot, so it cannot bound the gap here and wall time is
    /// the only evidence available. Crediting it is deliberate — a genuine overnight shutdown
    /// ought to count against the block. The cost is the accepted residual hole in spec §5.3
    /// (reboot, set the clock forward, boot), which closing would require a network time
    /// source inside an application whose job is severing network access.
    /// </summary>
    public static SessionTiming ResumeAfterRestart(SessionTiming timing, IClock clock)
    {
        var nowWall = clock.UtcNow;

        var gap = (long)(nowWall - timing.CheckpointWallUtc).TotalSeconds;
        if (gap < 0) gap = 0;

        return timing with
        {
            ElapsedSeconds = timing.ElapsedSeconds + gap,
            CheckpointWallUtc = nowWall,
            CheckpointMonotonicMs = clock.MonotonicMs,
        };
    }
}
