using Wolfstare.Core.Enforcement;

namespace Wolfstare.Enforcement.Machine;

/// <summary>
/// A Windows Firewall block rule for outbound DNS to anything but the service, used to seal the
/// DoH and rogue-resolver escape routes (spec §7.2). Present when the value is "present", absent
/// when null.
///
/// Uses ordinary firewall rules via netsh advfirewall rather than raw WFP: port-based egress
/// denial is expressible this way with a fraction of the P/Invoke surface, and the rules are
/// inspectable with netsh when debugging. Not unit-tested (spec §13).
/// </summary>
public sealed class FirewallRuleSetting(
    IProcessRunner runner,
    string ruleName,
    string direction,
    string protocol,
    string remotePort,
    string excludedProgram) : ISystemSetting
{
    public const string PresentMarker = "present";

    public string Key => $"firewall:{ruleName}";

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        var result = await runner.RunAsync(
            "netsh", $"advfirewall firewall show rule name=\"{ruleName}\"", ct);

        // netsh exits non-zero and prints "No rules match" when the rule is absent.
        return result.Succeeded && result.StandardOutput.Contains(ruleName, StringComparison.OrdinalIgnoreCase)
            ? PresentMarker
            : null;
    }

    public async Task WriteAsync(string? value, CancellationToken ct)
    {
        // Delete any existing rule first so applying is idempotent — netsh would otherwise add a
        // duplicate rule with the same name.
        await runner.RunAsync("netsh", Netsh.DeleteFirewallRuleArguments(ruleName), ct);

        if (value != PresentMarker) return;

        var result = await runner.RunAsync(
            "netsh",
            Netsh.AddFirewallBlockRuleArguments(ruleName, direction, protocol, remotePort, excludedProgram),
            ct);

        if (!result.Succeeded)
            throw new InvalidOperationException(
                $"Adding firewall rule '{ruleName}' failed ({result.ExitCode}): {result.StandardError.Trim()}");
    }
}
