using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging.Abstractions;
using Wolfstare.Core.Rules;
using Wolfstare.Core.Sessions;
using Wolfstare.Enforcement;
using Wolfstare.Service.Enforcement;
using Wolfstare.Service.Tests.Storage;

namespace Wolfstare.Service.Tests.Enforcement;

/// <summary>
/// Drives the enforcer with real in-memory collaborators but no privileged calls: ports are
/// ephemeral and system changes go to a recording fake. <see cref="WebsiteEnforcer.RefreshOnceAsync"/>
/// makes each test deterministic rather than waiting on the timer.
/// </summary>
public sealed class WebsiteEnforcerTests
{
    private readonly FakeClock _clock = new();
    private readonly InMemoryBlockListRepository _lists = new();
    private readonly InMemorySessionRepository _sessions = new();
    private readonly RuleSetCache _cache = new();
    private readonly RecordingSystemEnforcement _system = new();

    private SessionManager NewManager() => new(_lists, _sessions, new Pbkdf2PasswordHasher(), _clock);

    private WebsiteEnforcer NewEnforcer(SessionManager manager)
        => new(manager, _cache, _system, NullLogger<WebsiteEnforcer>.Instance);

    private async Task<Guid> GivenActiveDomainSession(SessionManager manager, string domain = "reddit.com")
    {
        var list = new BlockList(Guid.NewGuid(), "Focus", [new DomainRule(domain)], []);
        await _lists.SaveAsync(list);
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new TimedLock()));
        return list.Id;
    }

    [Fact]
    public async Task RefreshCopiesTheEffectiveRuleSetIntoTheCache()
    {
        var manager = NewManager();
        await GivenActiveDomainSession(manager);
        var enforcer = NewEnforcer(manager);

        await enforcer.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(Decision.Deny, _cache.Current.EvaluateDomain("reddit.com"));
    }

    [Fact]
    public async Task EndingTheLastSessionEmptiesTheCache()
    {
        var manager = NewManager();
        var list = new BlockList(Guid.NewGuid(), "Focus", [new DomainRule("reddit.com")], []);
        await _lists.SaveAsync(list);
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));
        var enforcer = NewEnforcer(manager);
        await enforcer.RefreshOnceAsync(CancellationToken.None);

        await manager.StopAsync(list.Id, null);
        await enforcer.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(Decision.Permit, _cache.Current.EvaluateDomain("reddit.com"));
    }

    [Fact]
    public async Task DomainRulesPresentAppliesSystemSettings()
    {
        var manager = NewManager();
        await GivenActiveDomainSession(manager);
        var enforcer = NewEnforcer(manager);

        await enforcer.RefreshOnceAsync(CancellationToken.None);

        Assert.True(_system.IsApplied);
    }

    [Fact]
    public async Task NoRulesRestoresSystemSettings()
    {
        // A stoppable (unlocked) session, since the point here is what happens when enforcement
        // disengages — a timed lock could not be stopped to reach that state.
        var manager = NewManager();
        var list = new BlockList(Guid.NewGuid(), "Focus", [new DomainRule("reddit.com")], []);
        await _lists.SaveAsync(list);
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));
        var enforcer = NewEnforcer(manager);
        await enforcer.RefreshOnceAsync(CancellationToken.None);
        Assert.True(_system.IsApplied);

        await manager.StopAsync(list.Id, null);
        await enforcer.RefreshOnceAsync(CancellationToken.None);

        Assert.False(_system.IsApplied);
        Assert.True(_system.Restored);
    }

    [Fact]
    public async Task SystemSettingsAreNotReAppliedEveryRefresh()
    {
        // The applier no-ops unchanged settings, but the enforcer should not even call it again
        // when the rule set has not changed.
        var manager = NewManager();
        await GivenActiveDomainSession(manager);
        var enforcer = NewEnforcer(manager);

        await enforcer.RefreshOnceAsync(CancellationToken.None);
        await enforcer.RefreshOnceAsync(CancellationToken.None);
        await enforcer.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(1, _system.ApplyCount);
    }

    [Fact]
    public async Task AppRuleAloneDoesNotTriggerWebsiteEnforcement()
    {
        // Website enforcement should only engage when there is something for it to do. An
        // app-only session must not hijack the machine's DNS.
        var manager = NewManager();
        var list = new BlockList(
            Guid.NewGuid(), "Games", [new AppRule(new ImageNameMatcher("steam.exe"))], []);
        await _lists.SaveAsync(list);
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));
        var enforcer = NewEnforcer(manager);

        await enforcer.RefreshOnceAsync(CancellationToken.None);

        Assert.False(_system.IsApplied);
    }

    [Fact]
    public async Task StartBindsTheSinkholeAndItBlocksAConfiguredDomain()
    {
        var manager = NewManager();
        await GivenActiveDomainSession(manager);
        var enforcer = NewEnforcer(manager);
        var options = new EnforcementOptions
        {
            ModifySystem = false,
            DnsPort = 0,
            ProxyPort = 0,
            TransparentHttpPort = 0,
            TransparentTlsPort = 0,
            Upstreams = ["1.1.1.1"],
        };

        await enforcer.StartServersAsync(options, CancellationToken.None);
        await enforcer.RefreshOnceAsync(CancellationToken.None);

        try
        {
            var response = await AskDns(enforcer.DnsEndpoint!, DnsQuery("reddit.com"));
            Assert.Equal(IPAddress.Loopback, new IPAddress(response[^4..]));
        }
        finally
        {
            await enforcer.StopServersAsync();
        }
    }

    [Fact]
    public async Task ABoundDnsPortDegradesHealthWithoutCrashing()
    {
        // Something already owns the port (ICS, Docker, another resolver). The service must
        // report degraded and keep serving, never fail open (spec §7.4).
        using var squatter = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var takenPort = ((IPEndPoint)squatter.Client.LocalEndPoint!).Port;

        var enforcer = NewEnforcer(NewManager());
        var health = new EnforcementHealth();
        var options = new EnforcementOptions
        {
            ModifySystem = false,
            DnsPort = takenPort,
            ProxyPort = 0,
            TransparentHttpPort = 0,
            TransparentTlsPort = 0,
            Upstreams = ["1.1.1.1"],
        };

        await enforcer.StartServersAsync(options, CancellationToken.None, health);

        try
        {
            Assert.Equal(EnforcementStatus.Degraded, health.Status);
            Assert.Contains("DNS", health.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await enforcer.StopServersAsync();
        }
    }

    /// <summary>A minimal type-A DNS query, enough to exercise the sinkhole end to end.</summary>
    private static byte[] DnsQuery(string name)
    {
        var bytes = new List<byte> { 0xBE, 0xEF, 0x01, 0x00, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };

        foreach (var label in name.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        bytes.AddRange(new byte[] { 0, 0, 1, 0, 1 }); // root, type A, class IN
        return bytes.ToArray();
    }

    private static async Task<byte[]> AskDns(IPEndPoint endpoint, byte[] query)
    {
        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        await client.SendAsync(query, endpoint);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        return (await client.ReceiveAsync(cts.Token)).Buffer;
    }

    [Fact]
    public async Task AppEnforcementSelfHealsByReApplyingEveryTick()
    {
        // If a blocked IFEO key is deleted while the service runs, the next tick must put it
        // back — so ApplyAsync is expected to run on every refresh while engaged, not once.
        var manager = NewManager();
        var app = new RecordingAppEnforcement();
        var list = new BlockList(
            Guid.NewGuid(), "Games", [new AppRule(new ImageNameMatcher("steam.exe"))], []);
        await _lists.SaveAsync(list);
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));

        var enforcer = new WebsiteEnforcer(manager, _cache, _system, NullLogger<WebsiteEnforcer>.Instance, app);

        await enforcer.RefreshOnceAsync(CancellationToken.None);
        await enforcer.RefreshOnceAsync(CancellationToken.None);
        await enforcer.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(3, app.ApplyCount);
    }

    [Fact]
    public async Task AppEnforcementReconcilesToNoAppRulesWhenTheSessionEnds()
    {
        // With the reconcile model, ending the session doesn't call a separate Restore — the next
        // ApplyAsync just receives rules with no app rule, and the reconcile removes the redirect.
        var manager = NewManager();
        var app = new RecordingAppEnforcement();
        var list = new BlockList(
            Guid.NewGuid(), "Games", [new AppRule(new ImageNameMatcher("steam.exe"))], []);
        await _lists.SaveAsync(list);
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));
        var enforcer = new WebsiteEnforcer(manager, _cache, _system, NullLogger<WebsiteEnforcer>.Instance, app);
        await enforcer.RefreshOnceAsync(CancellationToken.None);
        Assert.Equal(1, app.LastAppImageRuleCount);

        await manager.StopAsync(list.Id, null);
        await enforcer.RefreshOnceAsync(CancellationToken.None);

        Assert.Equal(0, app.LastAppImageRuleCount);   // reconcile told to want nothing → removes it
    }

    /// <summary>Records apply/restore calls so the ModifySystem decision can be asserted.</summary>
    private sealed class RecordingSystemEnforcement : ISystemEnforcement
    {
        public bool IsApplied { get; private set; }

        public bool Restored { get; private set; }

        public int ApplyCount { get; private set; }

        public Task ApplyAsync(RuleSet rules, CancellationToken ct)
        {
            IsApplied = true;
            Restored = false;
            ApplyCount++;
            return Task.CompletedTask;
        }

        public Task RestoreAsync(CancellationToken ct)
        {
            IsApplied = false;
            Restored = true;
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingAppEnforcement : IAppEnforcement
    {
        public int ApplyCount { get; private set; }

        public int LastAppImageRuleCount { get; private set; } = -1;

        public Task ApplyAsync(RuleSet rules, CancellationToken ct)
        {
            ApplyCount++;
            LastAppImageRuleCount = rules.Rules.OfType<AppRule>().Count(r => r.Matcher is ImageNameMatcher);
            return Task.CompletedTask;
        }

        public Task RestoreAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
