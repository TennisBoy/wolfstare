using Wolfstare.Core.Rules;

namespace Wolfstare.Enforcement.Tests;

public class RuleSetCacheTests
{
    [Fact]
    public void StartsEmpty()
    {
        var cache = new RuleSetCache();

        Assert.Equal(Decision.Permit, cache.Current.EvaluateDomain("reddit.com"));
    }

    [Fact]
    public void UpdateIsVisibleToSubsequentReads()
    {
        var cache = new RuleSetCache();

        cache.Update(new RuleSet([new DomainRule("reddit.com")], []));

        Assert.Equal(Decision.Deny, cache.Current.EvaluateDomain("reddit.com"));
    }

    [Fact]
    public void UpdatingWithNullIsRejected()
        => Assert.Throws<ArgumentNullException>(() => new RuleSetCache().Update(null!));

    [Fact]
    public async Task ConcurrentReadersNeverObserveNull()
    {
        var cache = new RuleSetCache();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var writer = Task.Run(() =>
        {
            var toggle = false;
            while (!cts.IsCancellationRequested)
            {
                cache.Update(toggle ? RuleSet.Empty : new RuleSet([new DomainRule("*")], []));
                toggle = !toggle;
            }
        });

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
                Assert.NotNull(cache.Current);
        }));

        await Task.WhenAll(readers.Append(writer));
    }
}
