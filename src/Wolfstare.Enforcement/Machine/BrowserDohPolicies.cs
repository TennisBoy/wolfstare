using Microsoft.Win32;
using Wolfstare.Core.Enforcement;

namespace Wolfstare.Enforcement.Machine;

/// <summary>
/// The registry policy values that turn DNS-over-HTTPS off in the major browsers (spec §7.2).
/// Without these the sinkhole is theatre: Chrome and Firefox resolve names over HTTPS by
/// default and never consult the system resolver.
///
/// These are enterprise policy keys under HKLM, so they apply to every user and survive a
/// browser update. Each is an ordinary <see cref="RegistryValueSetting"/>, so applying and
/// restoring them is journalled like any other change.
/// </summary>
public static class BrowserDohPolicies
{
    public static IReadOnlyList<ISystemSetting> All => new ISystemSetting[]
    {
        // Chrome / Edge: DnsOverHttpsMode = "off" (REG_SZ).
        new RegistryValueSetting(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Google\Chrome", "DnsOverHttpsMode"),
        new RegistryValueSetting(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "DnsOverHttpsMode"),

        // Firefox: DNSOverHTTPS\Enabled = 0 (REG_DWORD) disables TRR.
        new RegistryValueSetting(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Mozilla\Firefox\DNSOverHTTPS", "Enabled"),
    };

    /// <summary>The value each policy should hold while a session with domain rules is active.</summary>
    public static string DesiredValueFor(ISystemSetting policy) => policy.Key switch
    {
        var k when k.Contains("Firefox", StringComparison.OrdinalIgnoreCase) => "dword:0",
        _ => "string:off",
    };
}
