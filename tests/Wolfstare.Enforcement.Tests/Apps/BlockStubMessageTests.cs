using Wolfstare.Enforcement.Apps;

namespace Wolfstare.Enforcement.Tests.Apps;

/// <summary>
/// When IFEO redirects a launch to the stub, it passes the original executable path as the
/// first argument, followed by that program's own arguments. The stub has to pull a friendly
/// name out of that to show the user which app it just blocked.
/// </summary>
public class BlockStubMessageTests
{
    [Fact]
    public void PullsTheExeNameFromTheOriginalPath()
    {
        var message = BlockStubMessage.ForCommandLine([@"C:\Program Files\Steam\steam.exe"]);

        Assert.Contains("steam.exe", message);
    }

    [Fact]
    public void IgnoresTheOriginalProgramsOwnArguments()
    {
        var message = BlockStubMessage.ForCommandLine(
            [@"C:\Games\game.exe", "-fullscreen", "--profile", "me"]);

        Assert.Contains("game.exe", message);
        Assert.DoesNotContain("fullscreen", message);
    }

    [Fact]
    public void HandlesAPlainNameWithNoDirectory()
        => Assert.Contains("app.exe", BlockStubMessage.ForCommandLine(["app.exe"]));

    [Fact]
    public void FallsBackToAGenericNameWhenGivenNothing()
    {
        // A stub launched with no arguments should still say something coherent rather than
        // showing an empty or malformed message.
        var message = BlockStubMessage.ForCommandLine([]);

        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.Contains("Wolfstare", message);
    }

    [Fact]
    public void NamesWolfstareSoTheUserKnowsWhatBlockedIt()
        => Assert.Contains("Wolfstare", BlockStubMessage.ForCommandLine([@"C:\x\steam.exe"]));
}
