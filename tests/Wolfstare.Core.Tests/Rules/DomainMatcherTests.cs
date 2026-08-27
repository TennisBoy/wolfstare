using Wolfstare.Core.Rules;

namespace Wolfstare.Core.Tests.Rules;

public class DomainMatcherTests
{
    [Theory]
    [InlineData("Reddit.COM", "reddit.com")]
    [InlineData("reddit.com.", "reddit.com")]
    [InlineData("  reddit.com  ", "reddit.com")]
    [InlineData("münchen.de", "xn--mnchen-3ya.de")]
    public void NormalizeCanonicalisesHost(string input, string expected)
        => Assert.Equal(expected, DomainMatcher.Normalize(input));

    [Theory]
    [InlineData("*", "anything.example")]
    [InlineData("*", "reddit.com")]
    public void StarMatchesEverything(string pattern, string host)
        => Assert.True(DomainMatcher.Matches(pattern, host));

    [Theory]
    [InlineData("reddit.com", "reddit.com")]
    [InlineData("reddit.com", "www.reddit.com")]
    [InlineData("reddit.com", "a.b.reddit.com")]
    [InlineData("reddit.com", "REDDIT.com")]
    public void BareDomainMatchesApexAndSubdomains(string pattern, string host)
        => Assert.True(DomainMatcher.Matches(pattern, host));

    [Theory]
    [InlineData("reddit.com", "notreddit.com")]
    [InlineData("reddit.com", "reddit.com.evil.example")]
    [InlineData("reddit.com", "example.com")]
    public void BareDomainRespectsLabelBoundaries(string pattern, string host)
        => Assert.False(DomainMatcher.Matches(pattern, host));

    [Theory]
    [InlineData("*.reddit.com", "www.reddit.com")]
    [InlineData("*.reddit.com", "a.b.reddit.com")]
    public void WildcardMatchesSubdomains(string pattern, string host)
        => Assert.True(DomainMatcher.Matches(pattern, host));

    [Fact]
    public void WildcardDoesNotMatchApex()
        => Assert.False(DomainMatcher.Matches("*.reddit.com", "reddit.com"));

    [Theory]
    [InlineData("", "reddit.com")]
    [InlineData("reddit.com", "")]
    public void EmptyInputsDoNotMatch(string pattern, string host)
        => Assert.False(DomainMatcher.Matches(pattern, host));
}
