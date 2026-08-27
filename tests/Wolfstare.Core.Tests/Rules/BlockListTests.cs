using Wolfstare.Core.Rules;

namespace Wolfstare.Core.Tests.Rules;

public class BlockListTests
{
    private static BlockList List(
        string name = "Focus",
        BlockRule[]? rules = null,
        BlockRule[]? allowlist = null)
        => new(Guid.NewGuid(), name, rules ?? [new DomainRule("reddit.com")], allowlist ?? []);

    [Fact]
    public void ToRuleSetCarriesRulesAndAllowlist()
    {
        var list = List(
            rules: [new DomainRule("*")],
            allowlist: [new DomainRule("github.com")]);

        var set = list.ToRuleSet();

        Assert.Equal(Decision.Deny, set.EvaluateDomain("reddit.com"));
        Assert.Equal(Decision.Permit, set.EvaluateDomain("github.com"));
    }

    [Fact]
    public void WellFormedListIsValid()
        => Assert.True(BlockList.Validate(List()).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyNameIsRejected(string name)
    {
        var result = BlockList.Validate(List(name: name));

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Contains("name", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidRuleIsRejectedAndSurfacesTheValidatorMessage()
    {
        var result = BlockList.Validate(List(rules: [new AppRule(new ImageNameMatcher("explorer.exe"))]));

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Contains("explorer.exe", result.Error);
    }

    [Fact]
    public void InvalidAllowlistRuleIsAlsoRejected()
    {
        var result = BlockList.Validate(List(allowlist: [new DomainRule("http://example.com")]));

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Contains("example.com", result.Error);
    }

    [Fact]
    public void ListWithNoRulesIsRejected()
    {
        // An empty list would start a session that blocks nothing, which is almost certainly
        // a mistake rather than an intent.
        var result = BlockList.Validate(List(rules: []));

        Assert.False(result.IsValid);
    }
}
