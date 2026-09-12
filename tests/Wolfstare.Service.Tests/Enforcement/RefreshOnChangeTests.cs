using Microsoft.Extensions.Logging.Abstractions;
using Wolfstare.Core.Rules;
using Wolfstare.Core.Sessions;
using Wolfstare.Enforcement;
using Wolfstare.Service.Enforcement;
using Wolfstare.Service.Tests.Storage;

namespace Wolfstare.Service.Tests.Enforcement;

/// <summary>
/// The WebsiteEnforcer doubles as the immediate-refresh hook the API calls after a session
/// changes, so blocking takes effect at once rather than on the next timer tick.
/// </summary>
public sealed class RefreshOnChangeTests
{
    [Fact]
    public async Task RefreshNowUpdatesTheCacheImmediately()
    {
        var clock = new FakeClock();
        var lists = new InMemoryBlockListRepository();
        var sessions = new InMemorySessionRepository();
        var manager = new SessionManager(lists, sessions, new Pbkdf2PasswordHasher(), clock);
        var cache = new RuleSetCache();
        var enforcer = new WebsiteEnforcer(manager, cache, new NullSystemEnforcement(), NullLogger<WebsiteEnforcer>.Instance);

        var list = new BlockList(Guid.NewGuid(), "Focus", [new DomainRule("reddit.com")], []);
        await lists.SaveAsync(list);
        await manager.StartAsync(new SessionStartRequest(list.Id, 3600, new NoLock()));

        // The enforcer is the refresh hook.
        await ((IEnforcementRefresh)enforcer).RefreshNowAsync(CancellationToken.None);

        Assert.Equal(Decision.Deny, cache.Current.EvaluateDomain("reddit.com"));
    }

    [Fact]
    public async Task NullRefreshIsANoOp()
        => await new NullEnforcementRefresh().RefreshNowAsync(CancellationToken.None);
}
