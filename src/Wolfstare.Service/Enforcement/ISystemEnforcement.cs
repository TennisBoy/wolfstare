using System.Net.NetworkInformation;
using Microsoft.Extensions.Logging;
using Wolfstare.Core.Enforcement;
using Wolfstare.Core.Rules;
using Wolfstare.Core.Storage;
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
/// proxy, adds firewall egress rules, and disables browser DoH — all through the journalled
/// mutator, so <see cref="RestoreAsync"/> and startup crash-recovery put everything back
/// (spec §7).
///
/// One <see cref="Catalog"/> produces every setting and its desired value. It backs both apply
/// and the mutator's restore resolver, so a journalled key always maps back to the same setting
/// — the resolver and the applier can never disagree about a key's shape.
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
        IMutationJournal journal,
        IProcessRunner runner,
        string proxyServer,
        string serviceExecutablePath,
        string userSid,
        ILogger<WindowsSystemEnforcement> logger)
    {
        _logger = logger;
        _catalog = () => BuildCatalog(runner, proxyServer, serviceExecutablePath, userSid);

        // The resolver rebuilds the current catalog and finds the setting whose key matches, so
        // a restore always reconstructs the setting exactly as apply created it.
        _mutator = new JournalledMutator(
            journal,
            key => _catalog().Select(c => c.Setting).FirstOrDefault(s => s.Key == key));
    }

    public async Task ApplyAsync(RuleSet rules, CancellationToken ct)
    {
        foreach (var (setting, desired) in _catalog())
            await _mutator.ApplyAsync(setting, desired, ct);
    }

    public async Task RestoreAsync(CancellationToken ct)
    {
        var report = await _mutator.RestoreAllAsync(ct);

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
