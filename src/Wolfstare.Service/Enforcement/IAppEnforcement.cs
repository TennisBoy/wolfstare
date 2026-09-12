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
/// Installs an IFEO redirection for every image-name rule, through the same journalled mutator
/// as the website settings — so one <see cref="ISystemMutator.RestoreAllAsync"/> puts both
/// back, and a crash mid-change is recoverable.
///
/// Allowlisted names and critical processes are filtered out before construction: the IFEO
/// setting's constructor would throw on a critical name, and this keeps a single bad rule from
/// aborting the whole apply.
/// </summary>
public sealed class WindowsAppEnforcement(
    ISystemMutator mutator,
    string stubPath,
    ILogger<WindowsAppEnforcement> logger) : IAppEnforcement
{
    public async Task ApplyAsync(RuleSet rules, CancellationToken ct)
    {
        foreach (var imageName in ImageNamesToBlock(rules))
        {
            try
            {
                var setting = new ImageFileExecutionOptionsSetting(imageName, stubPath);
                await mutator.ApplyAsync(setting, stubPath, ct);
            }
            catch (ArgumentException ex)
            {
                // The critical-process guard in the setting's constructor. Should already be
                // filtered, but if a name slips through, skip it rather than abort the apply.
                logger.LogWarning(ex, "Skipping IFEO for a protected image name.");
            }
        }
    }

    public Task RestoreAsync(CancellationToken ct) => mutator.RestoreAllAsync(ct);

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
