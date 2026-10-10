using Wolfstare.Core.Sessions;

namespace Wolfstare.Core.Tests.Sessions;

/// <summary>
/// Telling the user where their retype went wrong. Only the first mismatch is ever reported:
/// reporting every wrong position would let a script recover the whole text in one pass per
/// alphabet letter, without reading the image.
/// </summary>
public class FirstMismatchTests
{
    [Fact]
    public void AnExactMatchHasNoMismatch()
        => Assert.Null(RandomText.FirstMismatchIndex("cat dog", "cat dog"));

    [Theory]
    [InlineData("cat dog", "bat dog", 0)]
    [InlineData("cat dog", "cat dug", 5)]
    [InlineData("cat dog", "cat doh", 6)]
    [InlineData("cat dog", "cat dxx", 5)]   // several wrong — only the first is reported
    public void ReportsTheFirstDifferingCharacter(string required, string attempt, int expected)
        => Assert.Equal(expected, RandomText.FirstMismatchIndex(required, attempt));

    [Fact]
    public void ACaseOnlyDifferenceIsAMismatch()
        => Assert.Equal(4, RandomText.FirstMismatchIndex("cat dog", "cat Dog"));

    [Fact]
    public void AShortAttemptIsWrongWhereItEnds()
        => Assert.Equal(3, RandomText.FirstMismatchIndex("cat dog", "cat"));

    [Fact]
    public void ALongAttemptIsWrongAtTheFirstExtraCharacter()
        => Assert.Equal(7, RandomText.FirstMismatchIndex("cat dog", "cat dogs"));

    [Fact]
    public void AnEmptyAttemptIsWrongAtTheStart()
        => Assert.Equal(0, RandomText.FirstMismatchIndex("cat dog", ""));
}
