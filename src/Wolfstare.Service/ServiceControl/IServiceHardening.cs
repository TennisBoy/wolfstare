using Microsoft.Extensions.Logging;
using Wolfstare.Enforcement.Machine;

namespace Wolfstare.Service.ServiceControl;

/// <summary>
/// Denies and restores the interactive user's ability to stop the Windows service while a
/// block is active (spec §9). Combined with the auto-restart failure actions set at install,
/// this is what makes the enforcement process hard to take down during a lock.
///
/// It only bites when Wolfstare is actually installed as a service; a dev console run has no
/// service descriptor to modify, and the Windows implementation degrades quietly there.
/// </summary>
public interface IServiceHardening
{
    Task DenyStopAsync(CancellationToken ct);

    Task AllowStopAsync(CancellationToken ct);
}

/// <summary>No-op: used in console/dev runs and tests, where there is no service to protect.</summary>
public sealed class NullServiceHardening : IServiceHardening
{
    public Task DenyStopAsync(CancellationToken ct) => Task.CompletedTask;

    public Task AllowStopAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Rewrites the service's security descriptor via <c>sc sdshow</c>/<c>sdset</c> using the
/// tested <see cref="ServiceSddl"/> transform. Not unit-tested — it shells out against a live
/// service (spec §13); the transform it applies is.
/// </summary>
public sealed class WindowsServiceHardening(
    IProcessRunner runner, string serviceName, ILogger<WindowsServiceHardening> logger) : IServiceHardening
{
    public Task DenyStopAsync(CancellationToken ct) => TransformAsync(ServiceSddl.DenyStop, ct);

    public Task AllowStopAsync(CancellationToken ct) => TransformAsync(ServiceSddl.AllowStop, ct);

    private async Task TransformAsync(Func<string, string> transform, CancellationToken ct)
    {
        var show = await runner.RunAsync("sc", $"sdshow {serviceName}", ct);
        if (!show.Succeeded)
        {
            // No such service (a console/dev run) — nothing to harden. Not an error.
            logger.LogDebug("sc sdshow {Service} did not return a descriptor; skipping.", serviceName);
            return;
        }

        var current = show.StandardOutput.Trim();
        var desired = transform(current);
        if (desired == current) return;

        var set = await runner.RunAsync("sc", $"sdset {serviceName} {desired}", ct);
        if (!set.Succeeded)
            logger.LogWarning("sc sdset {Service} failed: {Error}", serviceName, set.StandardError.Trim());
    }
}
