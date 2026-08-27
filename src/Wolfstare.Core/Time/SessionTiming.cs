namespace Wolfstare.Core.Time;

/// <summary>
/// The persisted timing state of an active session, checkpointed to the database every
/// 30 seconds so a crash or reboot loses at most that much accuracy.
/// </summary>
/// <param name="StartedAtUtc">When the session began. Informational; never used for expiry.</param>
/// <param name="ElapsedSeconds">Accrued time — the only value expiry is computed from.</param>
/// <param name="CheckpointWallUtc">Wall-clock reading at the last accrual.</param>
/// <param name="CheckpointMonotonicMs">Monotonic reading at the last accrual.</param>
public sealed record SessionTiming(
    DateTimeOffset StartedAtUtc,
    long ElapsedSeconds,
    DateTimeOffset CheckpointWallUtc,
    long CheckpointMonotonicMs)
{
    public static SessionTiming Start(IClock clock)
        => new(clock.UtcNow, 0, clock.UtcNow, clock.MonotonicMs);
}
