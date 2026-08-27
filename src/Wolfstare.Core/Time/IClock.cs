namespace Wolfstare.Core.Time;

/// <summary>
/// Time as the domain sees it — two independent readings, because a timed lock has to
/// survive the user changing the system clock. <see cref="UtcNow"/> is user-settable;
/// <see cref="MonotonicMs"/> counts forward since boot and cannot be set.
/// </summary>
public interface IClock
{
    DateTimeOffset UtcNow { get; }

    /// <summary>
    /// Milliseconds since system start. Resets to zero on reboot; never decreases otherwise.
    /// </summary>
    long MonotonicMs { get; }
}

/// <inheritdoc />
public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    public long MonotonicMs => Environment.TickCount64;
}
