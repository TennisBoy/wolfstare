using System.Text;

namespace Wolfstare.Enforcement.Machine;

/// <summary>An interface's DNS configuration, as read from netsh.</summary>
public sealed record DnsConfiguration(bool IsDhcp, IReadOnlyList<string> Servers);

/// <summary>
/// Pure construction of netsh command lines and parsing of its output. Separated from the
/// adapters that run the command so this logic — where an off-by-one in a server list or a
/// mis-parsed interface name is a real bug — is unit-tested without a live machine (spec §13).
/// </summary>
public static class Netsh
{
    public static string SetInterfaceDnsArguments(string interfaceName, string family, string primary)
        => $"interface {family} set dnsservers name=\"{interfaceName}\" source=static "
           + $"address={primary} register=none validate=no";

    public static string AddInterfaceDnsArguments(string interfaceName, string family, string server, int index)
        => $"interface {family} add dnsservers name=\"{interfaceName}\" address={server} index={index} validate=no";

    public static string SetInterfaceDnsDhcpArguments(string interfaceName, string family)
        => $"interface {family} set dnsservers name=\"{interfaceName}\" source=dhcp register=none validate=no";

    public static string ShowInterfaceDnsArguments(string interfaceName, string family)
        => $"interface {family} show dnsservers name=\"{interfaceName}\"";

    public static string AddFirewallBlockRuleArguments(
        string name, string direction, string protocol, string remotePort, string excludedProgram)
        => $"advfirewall firewall add rule name=\"{name}\" dir={direction} action=block "
           + $"protocol={protocol} remoteport={remotePort} program=\"{excludedProgram}\" enable=yes";

    public static string DeleteFirewallRuleArguments(string name)
        => $"advfirewall firewall delete rule name=\"{name}\"";

    /// <summary>
    /// Parses the output of `show dnsservers`. netsh lists the configured servers under one of
    /// two headers depending on whether they came from DHCP, then continues them on indented
    /// continuation lines.
    /// </summary>
    public static DnsConfiguration ParseDnsServers(string output)
    {
        var servers = new List<string>();
        var isDhcp = false;
        var inServerList = false;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            var headerIndex = line.IndexOf("DNS Servers", StringComparison.OrdinalIgnoreCase);
            if (line.Contains("configured through DHCP", StringComparison.OrdinalIgnoreCase))
            {
                isDhcp = true;
                inServerList = true;
                AddServer(servers, AfterColon(line));
            }
            else if (headerIndex >= 0 && line.Contains("Configured", StringComparison.OrdinalIgnoreCase))
            {
                inServerList = true;
                AddServer(servers, AfterColon(line));
            }
            else if (inServerList && (raw.StartsWith(' ') || raw.StartsWith('\t')) && !line.Contains(':'))
            {
                // A continuation line: an indented address with no "Header:" of its own.
                AddServer(servers, line.Trim());
            }
            else if (line.Contains(':'))
            {
                inServerList = false;
            }
        }

        return new DnsConfiguration(isDhcp, servers);
    }

    /// <summary>Serialises a configuration to the string stored as the setting's value.</summary>
    public static string ToSettingValue(DnsConfiguration config)
        => config.IsDhcp ? "dhcp" : string.Join(',', config.Servers);

    public static DnsConfiguration FromSettingValue(string? value)
    {
        if (string.Equals(value, "dhcp", StringComparison.OrdinalIgnoreCase))
            return new DnsConfiguration(IsDhcp: true, []);

        var servers = (value ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return new DnsConfiguration(IsDhcp: false, servers);
    }

    private static string AfterColon(string line)
    {
        var colon = line.IndexOf(':');
        return colon < 0 ? string.Empty : line[(colon + 1)..].Trim();
    }

    private static void AddServer(List<string> servers, string candidate)
    {
        if (candidate.Length == 0
            || candidate.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        servers.Add(candidate);
    }
}
