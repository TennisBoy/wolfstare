namespace Wolfstare.Service.Enforcement;

/// <summary>
/// Configuration for website enforcement, bound from the <c>Wolfstare:Enforcement</c> section.
///
/// <see cref="ModifySystem"/> defaults to false: the servers still run and can be tested by
/// pointing a client at their ports, but the machine's DNS, proxy, firewall, and browser
/// policy are left untouched. The installer turns it on. This is what keeps any automated run
/// from leaving a development machine without working DNS.
/// </summary>
public sealed class EnforcementOptions
{
    public const string Section = "Wolfstare:Enforcement";

    public bool ModifySystem { get; init; }

    public int DnsPort { get; init; } = 53;

    public int ProxyPort { get; init; } = 8080;

    public int TransparentHttpPort { get; init; } = 80;

    public int TransparentTlsPort { get; init; } = 443;

    public int RefreshSeconds { get; init; } = 2;

    /// <summary>Fallback resolvers, used only if the machine's own could not be captured.</summary>
    public IReadOnlyList<string> Upstreams { get; init; } = ["1.1.1.1", "8.8.8.8"];

    /// <summary>
    /// Full path to the block stub. Must be a world-runnable location (e.g. Program Files) so a
    /// standard user's blocked launch can execute it and see the block screen. Defaults to the
    /// stub next to the service executable when unset (fine for dev; the installer sets it).
    /// </summary>
    public string? StubPath { get; init; }
}
