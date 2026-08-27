using Wolfstare.Core.Time;

namespace Wolfstare.Core.Tests.Time;

/// <summary>
/// A clock whose wall time and monotonic counter move independently, so tests can simulate a
/// user changing the system clock while the monotonic counter keeps ticking — which is
/// exactly the situation the real implementation has to survive.
/// </summary>
public sealed class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; private set; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public long MonotonicMs { get; private set; }

    /// <summary>Honest passage of time: both readings advance together.</summary>
    public void Advance(TimeSpan amount)
    {
        UtcNow += amount;
        MonotonicMs += (long)amount.TotalMilliseconds;
    }

    /// <summary>The user changes the system clock. Monotonic is unaffected, as in reality.</summary>
    public void SetWallClock(DateTimeOffset value) => UtcNow = value;

    /// <summary>A reboot: monotonic resets to zero while wall time carries on.</summary>
    public void Reboot(TimeSpan downtime)
    {
        UtcNow += downtime;
        MonotonicMs = 0;
    }
}
