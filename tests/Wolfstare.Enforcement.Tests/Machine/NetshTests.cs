using Wolfstare.Enforcement.Machine;

namespace Wolfstare.Enforcement.Tests.Machine;

/// <summary>
/// The adapters that run netsh are not unit-tested — a mocked test of an external command
/// proves only that the mock was called (spec §13). But the argument construction and output
/// parsing are pure string work, and getting an interface name or a server list wrong is a
/// real bug, so those are tested here.
/// </summary>
public class NetshTests
{
    [Fact]
    public void SetStaticDnsArgumentsQuoteTheInterfaceName()
    {
        var args = Netsh.SetInterfaceDnsArguments("Wi-Fi 2", "ipv4", "127.0.0.1");

        Assert.Equal(
            "interface ipv4 set dnsservers name=\"Wi-Fi 2\" source=static address=127.0.0.1 register=none validate=no",
            args);
    }

    [Fact]
    public void SecondaryServersAreAddedInOrder()
    {
        var args = Netsh.AddInterfaceDnsArguments("Ethernet", "ipv4", "1.1.1.1", index: 2);

        Assert.Equal(
            "interface ipv4 add dnsservers name=\"Ethernet\" address=1.1.1.1 index=2 validate=no",
            args);
    }

    [Fact]
    public void RestoringToDhcpUsesTheDhcpSource()
    {
        var args = Netsh.SetInterfaceDnsDhcpArguments("Wi-Fi", "ipv4");

        Assert.Equal("interface ipv4 set dnsservers name=\"Wi-Fi\" source=dhcp register=none validate=no", args);
    }

    [Fact]
    public void ParsesStaticallyConfiguredServers()
    {
        // Output shape from `netsh interface ipv4 show dnsservers name="Wi-Fi"`.
        const string output =
            """
            Configuration for interface "Wi-Fi"
                Statically Configured DNS Servers:    192.168.1.1
                                                      1.1.1.1
                Register with which suffix:           Primary only
            """;

        var config = Netsh.ParseDnsServers(output);

        Assert.False(config.IsDhcp);
        Assert.Equal(["192.168.1.1", "1.1.1.1"], config.Servers);
    }

    [Fact]
    public void ParsesDhcpConfiguredServers()
    {
        const string output =
            """
            Configuration for interface "Ethernet"
                DNS servers configured through DHCP:  8.8.8.8
                Register with which suffix:           Primary only
            """;

        var config = Netsh.ParseDnsServers(output);

        Assert.True(config.IsDhcp);
        Assert.Equal(["8.8.8.8"], config.Servers);
    }

    [Fact]
    public void ParsesNoConfiguredServers()
    {
        const string output =
            """
            Configuration for interface "Wi-Fi"
                Statically Configured DNS Servers:    None
                Register with which suffix:           Primary only
            """;

        var config = Netsh.ParseDnsServers(output);

        Assert.False(config.IsDhcp);
        Assert.Empty(config.Servers);
    }

    [Fact]
    public void SettingValueRoundTripsThroughDhcpMarker()
    {
        // The ISystemSetting value is the string persisted in the journal. It must survive a
        // round trip so a restore reproduces exactly what was there.
        Assert.Equal("dhcp", Netsh.ToSettingValue(new DnsConfiguration(IsDhcp: true, [])));
        Assert.Equal("192.168.1.1,1.1.1.1", Netsh.ToSettingValue(new DnsConfiguration(false, ["192.168.1.1", "1.1.1.1"])));
        Assert.Equal("", Netsh.ToSettingValue(new DnsConfiguration(false, [])));
    }

    [Theory]
    [InlineData("dhcp", true)]
    [InlineData("192.168.1.1,1.1.1.1", false)]
    public void SettingValueParsesBack(string value, bool isDhcp)
    {
        var config = Netsh.FromSettingValue(value);
        Assert.Equal(isDhcp, config.IsDhcp);
    }

    [Fact]
    public void FirewallBlockRuleExcludesTheServiceExecutable()
    {
        var args = Netsh.AddFirewallBlockRuleArguments(
            "Wolfstare-DNS-Egress", "out", "UDP", "53", @"C:\Program Files\Wolfstare\Wolfstare.Service.exe");

        Assert.Contains("advfirewall firewall add rule", args);
        Assert.Contains("name=\"Wolfstare-DNS-Egress\"", args);
        Assert.Contains("dir=out", args);
        Assert.Contains("action=block", args);
        Assert.Contains("protocol=UDP", args);
        Assert.Contains("remoteport=53", args);
    }

    [Fact]
    public void DeletingAFirewallRuleUsesItsName()
        => Assert.Equal(
            "advfirewall firewall delete rule name=\"Wolfstare-DNS-Egress\"",
            Netsh.DeleteFirewallRuleArguments("Wolfstare-DNS-Egress"));
}
