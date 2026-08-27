using Wolfstare.Core.Time;

namespace Wolfstare.Core.Sessions;

/// <summary>
/// An active blocking session: one block list, one lock, one span of time.
/// </summary>
/// <param name="DurationSeconds">Null means indefinite — the session runs until stopped.</param>
public sealed record BlockSession(
    Guid Id,
    Guid BlockListId,
    SessionLock Lock,
    SessionTiming Timing,
    long? DurationSeconds)
{
    /// <summary>
    /// True once the session has served its full duration. Indefinite sessions never expire.
    ///
    /// This reads only from already-accrued time, never from a live clock — accrual is
    /// <see cref="ElapsedCalculator"/>'s job, and doing it in two places would let a caller
    /// that skipped the accrual step observe a different answer.
    /// </summary>
    public bool IsExpired()
        => DurationSeconds is { } duration && Timing.ElapsedSeconds >= duration;

    /// <summary>Seconds left, floored at zero. Null for an indefinite session.</summary>
    public long? RemainingSeconds()
        => DurationSeconds is { } duration
            ? Math.Max(0, duration - Timing.ElapsedSeconds)
            : null;
}
