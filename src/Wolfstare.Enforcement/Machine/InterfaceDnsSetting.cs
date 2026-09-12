using Wolfstare.Core.Enforcement;

namespace Wolfstare.Enforcement.Machine;

/// <summary>
/// One network interface's DNS servers, expressed as an <see cref="ISystemSetting"/> so the
/// takeover is journalled and reversible. The value is the comma-separated server list, or
/// "dhcp".
///
/// Not unit-tested — it runs netsh against the live machine (spec §13). Its argument
/// construction and output parsing live in <see cref="Netsh"/>, which is. The manual E2E
/// checklist in docs/manual-e2e-phase3.md covers the round trip on real hardware.
/// </summary>
public sealed class InterfaceDnsSetting(IProcessRunner runner, string interfaceName, string family = "ipv4")
    : ISystemSetting
{
    public string Key => $"dns:{family}:{interfaceName}";

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync("netsh", Netsh.ShowInterfaceDnsArguments(interfaceName, family), ct);
        if (!result.Succeeded) return null;

        return Netsh.ToSettingValue(Netsh.ParseDnsServers(result.StandardOutput));
    }

    public async Task WriteAsync(string? value, CancellationToken ct)
    {
        var config = Netsh.FromSettingValue(value);

        if (config.IsDhcp || config.Servers.Count == 0)
        {
            await Run(Netsh.SetInterfaceDnsDhcpArguments(interfaceName, family), ct);
            return;
        }

        await Run(Netsh.SetInterfaceDnsArguments(interfaceName, family, config.Servers[0]), ct);

        for (var i = 1; i < config.Servers.Count; i++)
            await Run(Netsh.AddInterfaceDnsArguments(interfaceName, family, config.Servers[i], index: i + 1), ct);
    }

    private async Task Run(string arguments, CancellationToken ct)
    {
        var result = await runner.RunAsync("netsh", arguments, ct);
        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"netsh {arguments} failed ({result.ExitCode}): {result.StandardError.Trim()}");
    }
}
