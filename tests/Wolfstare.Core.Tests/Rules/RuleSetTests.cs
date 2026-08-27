using Wolfstare.Core.Rules;

namespace Wolfstare.Core.Tests.Rules;

public class RuleSetTests
{
    private static RuleSet Set(BlockRule[] rules, BlockRule[]? allow = null)
        => new(rules, allow ?? []);

    [Fact]
    public void EmptySetPermitsEverything()
    {
        Assert.Equal(Decision.Permit, RuleSet.Empty.EvaluateDomain("reddit.com"));
        Assert.Equal(Decision.Permit, RuleSet.Empty.EvaluateApp(new("steam.exe", null, null)));
    }

    [Fact]
    public void MatchingDomainRuleDenies()
    {
        var set = Set([new DomainRule("reddit.com")]);

        Assert.Equal(Decision.Deny, set.EvaluateDomain("www.reddit.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("example.com"));
    }

    [Fact]
    public void AllowlistOverridesBlockRule()
    {
        var set = Set([new DomainRule("reddit.com")], [new DomainRule("old.reddit.com")]);

        Assert.Equal(Decision.Deny, set.EvaluateDomain("www.reddit.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("old.reddit.com"));
    }

    [Fact]
    public void WholeInternetExceptAllowlist()
    {
        var set = Set(
            [new DomainRule("*")],
            [new DomainRule("github.com"), new DomainRule("docs.microsoft.com")]);

        Assert.Equal(Decision.Deny, set.EvaluateDomain("reddit.com"));
        Assert.Equal(Decision.Deny, set.EvaluateDomain("news.ycombinator.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("github.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("api.github.com"));
    }

    [Fact]
    public void AppRulesAreEvaluatedIndependentlyOfDomainRules()
    {
        var set = Set([new AppRule(new ImageNameMatcher("steam.exe"))]);

        Assert.Equal(Decision.Deny, set.EvaluateApp(new("steam.exe", null, null)));
        Assert.Equal(Decision.Permit, set.EvaluateApp(new("notepad.exe", null, null)));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("steam.exe"));
    }

    [Fact]
    public void AllowlistOverridesAppRule()
    {
        var set = Set(
            [new AppRule(new PublisherMatcher("Valve"))],
            [new AppRule(new ImageNameMatcher("steam-cleanup.exe"))]);

        Assert.Equal(Decision.Deny, set.EvaluateApp(new("steam.exe", "CN=Valve Corp", null)));
        Assert.Equal(Decision.Permit, set.EvaluateApp(new("steam-cleanup.exe", "CN=Valve Corp", null)));
    }

    [Fact]
    public void PathRulesAreIgnoredByDomainEvaluationInV1()
    {
        // PathRule is modelled but not enforced until HTTPS inspection lands.
        var set = Set([new PathRule("reddit.com/r/all")]);

        Assert.Equal(Decision.Permit, set.EvaluateDomain("reddit.com"));
    }
}
