using Microsoft.Win32;
using Wolfstare.Core.Enforcement;
using Wolfstare.Enforcement.Machine;

namespace Wolfstare.Service.Enforcement;

/// <summary>
/// Reconstructs an <see cref="ISystemSetting"/> from a journal key. This is what makes restore
/// work after a crash: the journal holds only keys and original values, so putting a setting
/// back means rebuilding the setting object from its key alone, with no memory of having
/// created it.
///
/// One resolver covers every setting type — DNS, proxy, firewall, registry, IFEO — because the
/// journal is shared across website and app enforcement, and a single <c>RestoreAllAsync</c>
/// walks all of them. A key it cannot resolve is left journalled for a later run (spec §7.5).
/// </summary>
public sealed class SystemSettingResolver(
    IProcessRunner runner,
    string proxyServer,
    string stubPath,
    string serviceExecutablePath,
    string userSid)
{
    public ISystemSetting? Resolve(string key)
    {
        var prefix = key.Split(':', 2)[0];
        return prefix switch
        {
            "dns" => ResolveDns(key),
            "proxy" => new SystemProxySetting(userSid, proxyServer),
            "firewall" => ResolveFirewall(key),
            "registry" => ResolveRegistry(key),
            "ifeo" => ResolveIfeo(key),
            _ => null,
        };
    }

    private ISystemSetting? ResolveDns(string key)
    {
        // dns:{family}:{interfaceName}
        var parts = key.Split(':', 3);
        return parts.Length == 3 ? new InterfaceDnsSetting(runner, parts[2], parts[1]) : null;
    }

    private ISystemSetting? ResolveFirewall(string key)
    {
        // firewall:{ruleName}. The two rules are fixed, so restore rebuilds them by name; only
        // the name matters for the delete a restore performs.
        var ruleName = key.Split(':', 2).ElementAtOrDefault(1);
        return ruleName switch
        {
            "Wolfstare-DNS-Egress-UDP" => new FirewallRuleSetting(
                runner, ruleName, "out", "UDP", "53", serviceExecutablePath),
            "Wolfstare-DNS-Egress-DoT" => new FirewallRuleSetting(
                runner, ruleName, "out", "TCP", "853", serviceExecutablePath),
            _ => null,
        };
    }

    private static ISystemSetting? ResolveRegistry(string key)
    {
        // registry:{hive}:{subKeyPath}:{valueName}. Registry paths contain no colon, so a
        // four-way split is unambiguous.
        var parts = key.Split(':', 4);
        if (parts.Length != 4) return null;
        if (!Enum.TryParse<RegistryHive>(parts[1], out var hive)) return null;

        return new RegistryValueSetting(hive, parts[2], parts[3]);
    }

    private ISystemSetting? ResolveIfeo(string key)
    {
        // ifeo:{imageName}
        var imageName = key.Split(':', 2).ElementAtOrDefault(1);
        if (string.IsNullOrEmpty(imageName)) return null;

        // A critical name would throw in the constructor. It could never have been journalled,
        // but guard anyway rather than let a resolve throw.
        return Wolfstare.Core.Rules.CriticalProcesses.IsProtected(imageName)
            ? null
            : new ImageFileExecutionOptionsSetting(imageName, stubPath);
    }
}
