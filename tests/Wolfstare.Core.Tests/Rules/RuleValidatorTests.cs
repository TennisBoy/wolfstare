using Wolfstare.Core.Rules;

namespace Wolfstare.Core.Tests.Rules;

public class RuleValidatorTests
{
    [Theory]
    [InlineData("explorer.exe")]
    [InlineData("EXPLORER.EXE")]
    [InlineData("explorer")]
    [InlineData(@"C:\Windows\explorer.exe")]
    [InlineData("lsass.exe")]
    [InlineData("winlogon.exe")]
    [InlineData("csrss.exe")]
    [InlineData("services.exe")]
    [InlineData("wolfstare.service.exe")]
    public void CriticalProcessesAreProtected(string imageName)
        => Assert.True(CriticalProcesses.IsProtected(imageName));

    [Theory]
    [InlineData("steam.exe")]
    [InlineData("4kvideodownloaderplus.exe")]
    [InlineData("discord.exe")]
    public void OrdinaryApplicationsAreNotProtected(string imageName)
        => Assert.False(CriticalProcesses.IsProtected(imageName));

    [Fact]
    public void RuleTargetingCriticalProcessIsRejectedWithAnActionableMessage()
    {
        var result = RuleValidator.Validate(new AppRule(new ImageNameMatcher("explorer.exe")));

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Contains("explorer.exe", result.Error);
        Assert.Contains("critical", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void OrdinaryAppRuleIsAccepted()
        => Assert.True(RuleValidator.Validate(new AppRule(new ImageNameMatcher("steam.exe"))).IsValid);

    [Fact]
    public void PathRuleIsRejectedInV1()
    {
        var result = RuleValidator.Validate(new PathRule("reddit.com/r/all"));

        Assert.False(result.IsValid);
        Assert.NotNull(result.Error);
        Assert.Contains("HTTPS inspection", result.Error);
    }

    [Theory]
    [InlineData("reddit.com")]
    [InlineData("*.reddit.com")]
    [InlineData("*")]
    public void ValidDomainRulesAreAccepted(string pattern)
        => Assert.True(RuleValidator.Validate(new DomainRule(pattern)).IsValid);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not a domain")]
    [InlineData("http://reddit.com")]
    [InlineData("reddit.com/r/all")]
    [InlineData("localhost")]
    [InlineData("*.")]
    [InlineData("red*it.com")]
    public void MalformedDomainRulesAreRejected(string pattern)
        => Assert.False(RuleValidator.Validate(new DomainRule(pattern)).IsValid);

    [Fact]
    public void PublisherRuleThatWouldCatchMicrosoftIsRejected()
    {
        // A publisher rule for "Microsoft" would have the ETW watcher terminating most of
        // the operating system.
        var result = RuleValidator.Validate(new AppRule(new PublisherMatcher("Microsoft Windows")));

        Assert.False(result.IsValid);
    }

    [Fact]
    public void EmptyMatcherValuesAreRejected()
    {
        Assert.False(RuleValidator.Validate(new AppRule(new ImageNameMatcher(""))).IsValid);
        Assert.False(RuleValidator.Validate(new AppRule(new PublisherMatcher("  "))).IsValid);
        Assert.False(RuleValidator.Validate(new AppRule(new FileDescriptionMatcher(""))).IsValid);
    }
}
