using Microsoft.Extensions.Logging;
using Wolfstare.Core.Enforcement;
using Wolfstare.Core.Rules;
using Wolfstare.Enforcement.Machine;

namespace Wolfstare.Service.Enforcement;

/// <summary>
/// Applies and restores the IFEO redirections that block applications by name (spec §8.1).
/// Publisher and description rules carry no filename, so they are not expressed here — the ETW
/// watcher enforces those.
/// </summary>
public interface IAppEnforcement
{
    Task ApplyAsync(RuleSet rules, CancellationToken ct);

    Task RestoreAsync(CancellationToken ct);
}

/// <summary>The <c>ModifySystem=false</c> implementation: no registry keys are written.</summary>
public sealed class NullAppEnforcement : IAppEnforcement
{
    public Task ApplyAsync(RuleSet rules, CancellationToken ct) => Task.CompletedTask;

    public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Reconciles the IFEO redirections against the active blocks. <see cref="ApplyAsync"/> makes
/// reality match the rules: it (re)writes a redirect for every image-name rule and — crucially —
/// removes any redirect Wolfstare owns that no active block calls for. That removal is what
/// stops an orphaned key stranding an app as blocked with no session to unlock: the enforcer
/// runs this every tick, so a stale redirect is gone within a refresh interval regardless of how
/// it was left behind (a mid-block restart, a crash, whatever).
///
/// It finds "its own" redirects by their Debugger value pointing at the stub — not by a journal
/// or an in-memory flag — so it is self-correcting even across restarts.
/// </summary>
public sealed class WindowsAppEnforcement : IAppEnforcement
{
    private readonly ISystemMutator _mutator;
    private readonly string _stubPath;
    private readonly ILogger<WindowsAppEnforcement> _logger;
    private readonly Func<IReadOnlyList<string>> _existingRedirects;

    public WindowsAppEnforcement(
        ISystemMutator mutator,
        string stubPath,
        ILogger<WindowsAppEnforcement> logger,
        Func<IReadOnlyList<string>>? existingRedirects = null)
    {
        _mutator = mutator;
        _stubPath = stubPath;
        _logger = logger;
        _existingRedirects = existingRedirects ?? (() => ImageFileExecutionOptionsSetting.RedirectedTo(stubPath));
    }

    public async Task ApplyAsync(RuleSet rules, CancellationToken ct)
    {
        var desired = ImageNamesToBlock(rules).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Write (or confirm) every wanted redirect.
        foreach (var imageName in desired)
            await SetAsync(imageName, _stubPath, ct);

        // Remove every redirect we own that is no longer wanted — the reconcile that clears orphans.
        foreach (var imageName in _existingRedirects().Select(ImageNameMatcher.Canonical).Distinct(StringComparer.OrdinalIgnoreCase))
            if (!desired.Contains(imageName))
                await SetAsync(imageName, null, ct);
    }

    /// <summary>Removes every redirect Wolfstare owns (reconcile to "nothing blocked").</summary>
    public Task RestoreAsync(CancellationToken ct) => ApplyAsync(RuleSet.Empty, ct);

    private async Task SetAsync(string imageName, string? desired, CancellationToken ct)
    {
        try
        {
            await _mutator.ApplyAsync(new ImageFileExecutionOptionsSetting(imageName, _stubPath), desired, ct);
        }
        catch (ArgumentException ex)
        {
            // The critical-process guard in the setting's constructor. Should already be
            // filtered, but if a name slips through, skip it rather than abort the reconcile.
            _logger.LogWarning(ex, "Skipping IFEO for a protected image name.");
        }
    }

    private static IEnumerable<string> ImageNamesToBlock(RuleSet rules)
    {
        var names = rules.Rules
            .OfType<AppRule>()
            .Select(r => r.Matcher)
            .OfType<ImageNameMatcher>()
            .Select(m => m.ImageName)
            .Where(name => !CriticalProcesses.IsProtected(name))
            .Select(ImageNameMatcher.Canonical)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        // An allowlisted image name must not be IFEO-blocked. Evaluate each candidate against
        // the full rule set so allowlist precedence is honoured exactly as everywhere else.
        return names.Where(name => rules.EvaluateApp(new ProcessIdentity(name, null, null)) == Decision.Deny);
    }
}
