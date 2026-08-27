using Wolfstare.Core.Sessions;

namespace Wolfstare.Service;

/// <summary>
/// Drives time accrual for active sessions.
///
/// Resumes once at startup — crediting downtime from the wall clock, since the monotonic
/// counter reset at boot (spec §5.2) — then ticks on the checkpoint interval. The interval
/// bounds how much accuracy a crash can cost, not how much time can be accrued: the
/// calculator is interval-independent.
/// </summary>
public sealed class SessionTicker(SessionManager manager, ILogger<SessionTicker> logger)
    : BackgroundService
{
    private static readonly TimeSpan CheckpointInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await manager.ResumeAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            // Fail closed: a resume that throws leaves the sessions in place rather than
            // clearing them, so blocks survive a startup problem.
            logger.LogError(ex, "Failed to resume sessions at startup; existing sessions remain active.");
        }

        using var timer = new PeriodicTimer(CheckpointInterval);

        while (await SafeWaitAsync(timer, stoppingToken))
        {
            try
            {
                await manager.TickAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Session tick failed; will retry on the next interval.");
            }
        }
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
