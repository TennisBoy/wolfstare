using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Wolfstare.Core.Enforcement;
using Wolfstare.Core.Rules;
using Wolfstare.Enforcement.Machine;

namespace Wolfstare.Service.Enforcement;

/// <summary>
/// Applies or restores the machine-level configuration that backs website enforcement — DNS
/// takeover, proxy registration, firewall egress rules, browser DoH policy.
/// </summary>
public interface ISystemEnforcement
{
    Task ApplyAsync(RuleSet rules, CancellationToken ct);

    Task RestoreAsync(CancellationToken ct);
}

/// <summary>
/// The <c>ModifySystem=false</c> implementation: the servers run, but the machine is left
/// alone. This is the default, so no test or dev run can strand a machine's networking.
/// </summary>
public sealed class NullSystemEnforcement : ISystemEnforcement
{
    public Task ApplyAsync(RuleSet rules, CancellationToken ct) => Task.CompletedTask;

    public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// The real implementation. Points every active interface's DNS at the sinkhole, registers the
/// proxy, adds firewall egress rules, and disables browser DoH — all through the shared
/// journalled mutator, so <see cref="RestoreAsync"/> and startup crash-recovery put everything
/// back (spec §7).
///
/// The mutator is shared with app enforcement: both write to one journal, and one
/// <c>RestoreAllAsync</c> covers both. Restore reconstruction is handled by
/// <see cref="SystemSettingResolver"/>, not here.
///
/// Not unit-tested: it enumerates live interfaces and drives the untested Windows settings. The
/// mutator it delegates to is tested; the manual E2E checklist covers this on real hardware.
/// </summary>
public sealed class WindowsSystemEnforcement : ISystemEnforcement
{
    private readonly ISystemMutator _mutator;
    private readonly ILogger<WindowsSystemEnforcement> _logger;
    private readonly Func<IReadOnlyList<(ISystemSetting Setting, string? Desired)>> _catalog;

    public WindowsSystemEnforcement(
        ISystemMutator mutator,
        IProcessRunner runner,
        string proxyServer,
        string serviceExecutablePath,
        string userSid,
        ILogger<WindowsSystemEnforcement> logger)
    {
        _mutator = mutator;
        _logger = logger;
        _catalog = () => BuildCatalog(runner, proxyServer, serviceExecutablePath, userSid);
    }

    public async Task ApplyAsync(RuleSet rules, CancellationToken ct)
    {
        foreach (var (setting, desired) in _catalog())
            await _mutator.ApplyAsync(setting, desired, ct);
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        // Only the website settings — IFEO keys belong to app enforcement and may still be
        // engaged when website enforcement disengages.
        var report = await _mutator.RestoreMatchingAsync(key => !key.StartsWith("ifeo:", StringComparison.Ordinal), ct);

        if (!report.IsComplete)
            _logger.LogWarning(
                "System restore incomplete: {Failed} failed, {Unresolved} unresolved. They remain journalled for retry.",
                report.Failed.Count, report.Unresolved.Count);
    }

    private static IReadOnlyList<(ISystemSetting, string?)> BuildCatalog(
        IProcessRunner runner, string proxyServer, string serviceExecutablePath, string userSid)
    {
        var catalog = new List<(ISystemSetting, string?)>();

        foreach (var name in ActiveInterfaceNames())
        {
            catalog.Add((new InterfaceDnsSetting(runner, name, "ipv4"), "127.0.0.1"));
            catalog.Add((new InterfaceDnsSetting(runner, name, "ipv6"), "::1"));
        }

        var proxy = new SystemProxySetting(userSid, proxyServer);
        catalog.Add((proxy, proxy.DesiredValue));

        catalog.Add((
            new FirewallRuleSetting(runner, "Wolfstare-DNS-Egress-UDP", "out", "UDP", "53", serviceExecutablePath),
            FirewallRuleSetting.PresentMarker));
        catalog.Add((
            new FirewallRuleSetting(runner, "Wolfstare-DNS-Egress-DoT", "out", "TCP", "853", serviceExecutablePath),
            FirewallRuleSetting.PresentMarker));

        foreach (var policy in BrowserDohPolicies.All)
            catalog.Add((policy, BrowserDohPolicies.DesiredValueFor(policy)));

        return catalog;
    }

    private static IEnumerable<string> ActiveInterfaceNames()
    {
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up) continue;
            if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            yield return nic.Name;
        }
    }
}
