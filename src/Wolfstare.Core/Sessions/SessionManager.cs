using Wolfstare.Core.Rules;
using Wolfstare.Core.Storage;
using Wolfstare.Core.Time;

namespace Wolfstare.Core.Sessions;

/// <summary>A request to begin blocking against one block list.</summary>
/// <param name="DurationSeconds">Null means indefinite. Required for a <see cref="TimedLock"/>.</param>
public sealed record SessionStartRequest(Guid BlockListId, long? DurationSeconds, SessionLock Lock);

/// <summary>Why a session could not be started.</summary>
public enum StartFailure
{
    None,
    BlockListNotFound,
    AlreadyActive,

    /// <summary>
    /// A timed lock was requested without a duration. Such a session could never be ended by
    /// anything at all, which is a footgun rather than a feature.
    /// </summary>
    TimedLockNeedsDuration,
}

public readonly record struct StartResult(BlockSession? Session, StartFailure Failure)
{
    public static StartResult Fail(StartFailure failure) => new(null, failure);

    public static StartResult Ok(BlockSession session) => new(session, StartFailure.None);
}

/// <summary>
/// Owns the lifecycle of active sessions: starting, accruing time, expiring, and stopping.
///
/// This is the only type that mutates session state, and every stop request goes through
/// <see cref="StopPolicy"/> — the manager never re-derives lock semantics for itself. Time is
/// accrued in exactly one place (<see cref="TickAsync"/> and <see cref="ResumeAsync"/>), so no
/// caller can observe a different elapsed value by computing its own.
/// </summary>
public sealed class SessionManager(
    IBlockListRepository lists,
    ISessionRepository sessions,
    IPasswordHasher hasher,
    IClock clock)
{
    public async Task<StartResult> StartAsync(SessionStartRequest request, CancellationToken ct = default)
    {
        if (request.Lock is TimedLock && request.DurationSeconds is null)
            return StartResult.Fail(StartFailure.TimedLockNeedsDuration);

        var list = await lists.GetAsync(request.BlockListId, ct);
        if (list is null)
            return StartResult.Fail(StartFailure.BlockListNotFound);

        var active = await sessions.GetActiveAsync(ct);
        if (active.Any(s => s.BlockListId == request.BlockListId))
            return StartResult.Fail(StartFailure.AlreadyActive);

        var session = new BlockSession(
            Guid.NewGuid(),
            request.BlockListId,
            request.Lock,
            SessionTiming.Start(clock),
            request.DurationSeconds);

        await sessions.SaveAsync(session, ct);
        return StartResult.Ok(session);
    }

    /// <summary>
    /// Attempts to stop the active session for a block list. A list with no active session is
    /// already in the requested state, so that reports <see cref="StopOutcome.Allowed"/>.
    /// </summary>
    public async Task<StopOutcome> StopAsync(Guid blockListId, string? password, CancellationToken ct = default)
    {
        var session = (await sessions.GetActiveAsync(ct))
            .FirstOrDefault(s => s.BlockListId == blockListId);

        if (session is null) return StopOutcome.Allowed;

        // Accrue before deciding: a session that has just expired should be stoppable, and
        // the caller must not have to tick first to observe that.
        session = session with { Timing = ElapsedCalculator.Advance(session.Timing, clock) };

        var outcome = StopPolicy.CanStop(session, password, hasher, clock);

        if (outcome == StopOutcome.Allowed)
            await sessions.RemoveAsync(session.Id, ct);
        else
            await sessions.SaveAsync(session, ct);

        return outcome;
    }

    /// <summary>
    /// Accrues time for every active session, persists the checkpoint, and ends any that have
    /// expired. Called on a timer by the service.
    /// </summary>
    public async Task TickAsync(CancellationToken ct = default)
        => await AccrueAsync(ElapsedCalculator.Advance, ct);

    /// <summary>
    /// Resumes sessions after a service restart, crediting downtime from the wall clock
    /// because the monotonic counter has reset (spec §5.2).
    /// </summary>
    public async Task ResumeAsync(CancellationToken ct = default)
        => await AccrueAsync(ElapsedCalculator.ResumeAfterRestart, ct);

    private async Task AccrueAsync(Func<SessionTiming, IClock, SessionTiming> accrue, CancellationToken ct)
    {
        foreach (var session in await sessions.GetActiveAsync(ct))
        {
            var updated = session with { Timing = accrue(session.Timing, clock) };

            if (updated.IsExpired())
                await sessions.RemoveAsync(updated.Id, ct);
            else
                await sessions.SaveAsync(updated, ct);
        }
    }

    public async Task<IReadOnlyList<BlockSession>> GetActiveAsync(CancellationToken ct = default)
        => await sessions.GetActiveAsync(ct);

    /// <summary>
    /// The union of every active session's rules, and of their allowlists — the rule set the
    /// enforcement layer applies.
    ///
    /// Unioning allowlists is deliberate: an allowlist entry is a statement that the user
    /// needs that host, and honouring it across concurrent sessions avoids one list silently
    /// breaking another's exception.
    /// </summary>
    public async Task<RuleSet> GetEffectiveRuleSetAsync(CancellationToken ct = default)
    {
        var active = await sessions.GetActiveAsync(ct);
        if (active.Count == 0) return RuleSet.Empty;

        var rules = new List<BlockRule>();
        var allowlist = new List<BlockRule>();

        foreach (var session in active)
        {
            // A session whose list was deleted contributes nothing. It is not an error: the
            // session is still ticking down and will end on its own terms.
            var list = await lists.GetAsync(session.BlockListId, ct);
            if (list is null) continue;

            rules.AddRange(list.Rules);
            allowlist.AddRange(list.Allowlist);
        }

        return new RuleSet(rules, allowlist);
    }
}
