using Wolfstare.Enforcement.Apps;

namespace Wolfstare.Enforcement.Tests.Apps;

/// <summary>
/// Identity resolution runs against real files on disk — no ETW, no admin.
///
/// The signed-binary case targets <c>dotnet.exe</c> rather than a system binary like
/// notepad.exe on purpose: most OS binaries are *catalog*-signed (the signature lives in a
/// separate .cat file, not the PE), and Authenticode extraction only sees *embedded*
/// signatures. That is not a gap for us — the third-party apps a user blocks (Steam, 4K Video
/// Downloader Plus, and the like) embed their signatures, while catalog-signed OS binaries are
/// Microsoft-published and already barred from being blocked. dotnet.exe is embedded-signed and
/// guaranteed present, since the tests are built with it.
/// </summary>
public class ProcessInspectorTests
{
    private static readonly string? DotnetExe = LocateDotnet();

    [Fact]
    public void ResolvesImageNameFromPath()
    {
        var identity = ProcessInspector.FromImagePath(@"C:\Program Files\Steam\steam.exe");

        Assert.Equal(@"C:\Program Files\Steam\steam.exe", identity.ImageName);
    }

    [Fact]
    public void EmbeddedSignedBinaryHasAPublisher()
    {
        Assert.NotNull(DotnetExe);

        var publisher = ProcessInspector.PublisherOf(DotnetExe);

        Assert.NotNull(publisher);
        Assert.Contains("Microsoft", publisher, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SignedBinaryHasADescription()
    {
        Assert.NotNull(DotnetExe);

        Assert.False(string.IsNullOrWhiteSpace(ProcessInspector.DescriptionOf(DotnetExe)));
    }

    [Fact]
    public void FromImagePathPopulatesPublisherForAnEmbeddedSignedBinary()
    {
        Assert.NotNull(DotnetExe);

        var identity = ProcessInspector.FromImagePath(DotnetExe);

        Assert.EndsWith("dotnet.exe", identity.ImageName, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(identity.Publisher);
    }

    [Fact]
    public void UnsignedFileHasNoPublisherOrDescription()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wolfstare-unsigned-{Guid.NewGuid():N}.exe");
        File.WriteAllBytes(path, [0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03]); // MZ header, then junk

        try
        {
            Assert.Null(ProcessInspector.PublisherOf(path));
            Assert.Null(ProcessInspector.DescriptionOf(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MissingFileResolvesToNullsRatherThanThrowing()
    {
        var identity = ProcessInspector.FromImagePath(@"C:\does\not\exist\ghost.exe");

        Assert.Equal(@"C:\does\not\exist\ghost.exe", identity.ImageName);
        Assert.Null(identity.Publisher);
        Assert.Null(identity.FileDescription);
    }

    private static string? LocateDotnet()
    {
        var wellKnown = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (File.Exists(wellKnown)) return wellKnown;

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            var candidate = Path.Combine(dir.Trim(), "dotnet.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }
}
