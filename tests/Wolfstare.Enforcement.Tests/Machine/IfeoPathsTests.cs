using Wolfstare.Enforcement.Machine;

namespace Wolfstare.Enforcement.Tests.Machine;

public class IfeoPathsTests
{
    [Fact]
    public void BuildsNativeAndWow64Paths()
    {
        var paths = IfeoPaths.For("4kvideodownloaderplus.exe");

        Assert.Equal(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\4kvideodownloaderplus.exe",
            paths.Native);
        Assert.Equal(
            @"SOFTWARE\WOW6432Node\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\4kvideodownloaderplus.exe",
            paths.Wow64);
    }

    [Fact]
    public void CanonicalisesTheImageName()
    {
        // A rule author may write the name without its extension, or with different casing.
        // The IFEO key Windows consults is the bare lowercase filename.
        var fromBare = IfeoPaths.For("Steam");
        var fromPath = IfeoPaths.For(@"C:\Program Files\Steam\STEAM.EXE");

        Assert.EndsWith(@"Image File Execution Options\steam.exe", fromBare.Native);
        Assert.Equal(fromBare.Native, fromPath.Native);
    }

    [Fact]
    public void ConstructingASettingForACriticalProcessThrows()
    {
        // The guard at the enforcement boundary — even if a bad rule reached here, an IFEO key
        // on explorer.exe would leave the machine with no shell (spec §8.3).
        var ex = Assert.Throws<ArgumentException>(
            () => new ImageFileExecutionOptionsSetting("explorer.exe", @"C:\stub.exe"));

        Assert.Contains("explorer.exe", ex.Message);
    }

    [Fact]
    public void ConstructingASettingForAnOrdinaryAppIsAllowed()
    {
        var setting = new ImageFileExecutionOptionsSetting("steam.exe", @"C:\stub.exe");

        Assert.Equal("ifeo:steam.exe", setting.Key);
    }
}
