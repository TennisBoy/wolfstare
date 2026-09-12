namespace Wolfstare.Service.Enforcement;

/// <summary>
/// A hook the API calls right after a session starts, stops, or unlocks, so enforcement
/// reconciles immediately instead of on the next background tick. Without it, a block took
/// effect up to one refresh interval late.
/// </summary>
public interface IEnforcementRefresh
{
    Task RefreshNowAsync(CancellationToken ct);
}

/// <summary>Used when a host has no enforcement wired (some tests). Does nothing.</summary>
public sealed class NullEnforcementRefresh : IEnforcementRefresh
{
    public Task RefreshNowAsync(CancellationToken ct) => Task.CompletedTask;
}
