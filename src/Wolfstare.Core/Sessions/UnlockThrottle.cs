using System.Collections.Concurrent;
using Wolfstare.Core.Time;

namespace Wolfstare.Core.Sessions;

/// <summary>
/// A per-block-list cooldown after a wrong unlock retype.
///
/// Telling the user where their retype first went wrong (<see cref="RandomText.FirstMismatchIndex"/>)
/// is an oracle: a script could guess one character at a time and watch the index advance,
/// recovering the text without ever reading the challenge image. A few seconds between attempts
/// costs a human nothing — they are fixing a typo — but makes that guessing slower than typing.
///
/// Timed on <see cref="IClock.MonotonicMs"/>, which the user cannot set, so moving the system
/// clock does not skip it. State is in memory: a service restart clears it, which is harmless
/// because a restart takes longer than a cooldown.
/// </summary>
public sealed class UnlockThrottle
{
    private readonly IClock _clock;
    private readonly long _cooldownMs;
    private readonly ConcurrentDictionary<Guid, long> _failedAtMs = new();

    public UnlockThrottle(IClock clock, TimeSpan cooldown)
    {
        _clock = clock;
        _cooldownMs = (long)cooldown.TotalMilliseconds;
    }

    /// <summary>How long until the list accepts another attempt, or null if it does now.</summary>
    public TimeSpan? Remaining(Guid blockListId)
    {
        if (!_failedAtMs.TryGetValue(blockListId, out var failedAt)) return null;

        // A counter that somehow went backwards would otherwise hold the cooldown until it caught
        // up with the old reading. Re-anchor instead, so the wait is never longer than one
        // cooldown from now.
        var now = _clock.MonotonicMs;
        if (now < failedAt)
        {
            _failedAtMs[blockListId] = now;
            failedAt = now;
        }

        var remaining = _cooldownMs - (now - failedAt);

        return remaining > 0 ? TimeSpan.FromMilliseconds(remaining) : null;
    }

    public void RecordFailure(Guid blockListId) => _failedAtMs[blockListId] = _clock.MonotonicMs;
}
