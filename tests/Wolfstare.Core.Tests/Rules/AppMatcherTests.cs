using Wolfstare.Core.Rules;

namespace Wolfstare.Core.Tests.Rules;

public class AppMatcherTests
{
    private static ProcessIdentity Identity(
        string image = "app.exe", string? publisher = null, string? description = null)
        => new(image, publisher, description);

    [Theory]
    [InlineData("4kvideodownloaderplus.exe", "4kvideodownloaderplus.exe")]
    [InlineData("4KVideoDownloaderPlus.EXE", "4kvideodownloaderplus.exe")]
    [InlineData("4kvideodownloaderplus", "4kvideodownloaderplus.exe")]
    public void ImageNameMatchesCaseInsensitivelyAndToleratesMissingExtension(
        string pattern, string actual)
        => Assert.True(new ImageNameMatcher(pattern).Matches(Identity(image: actual)));

    [Fact]
    public void ImageNameDoesNotMatchDifferentExecutable()
        => Assert.False(new ImageNameMatcher("steam.exe").Matches(Identity(image: "notepad.exe")));

    [Fact]
    public void ImageNameIgnoresAnyPathSuppliedByTheCaller()
        => Assert.True(new ImageNameMatcher("steam.exe")
            .Matches(Identity(image: @"C:\Program Files\Steam\steam.exe")));

    [Fact]
    public void PublisherMatchesAsCaseInsensitiveSubstring()
        => Assert.True(new PublisherMatcher("Open Media")
            .Matches(Identity(publisher: "CN=Open Media LLC, O=Open Media LLC, C=CY")));

    [Fact]
    public void PublisherDoesNotMatchWhenIdentityIsUnsigned()
        => Assert.False(new PublisherMatcher("Open Media").Matches(Identity(publisher: null)));

    [Fact]
    public void FileDescriptionMatchesAsCaseInsensitiveSubstring()
        => Assert.True(new FileDescriptionMatcher("video downloader")
            .Matches(Identity(description: "4K Video Downloader Plus")));

    [Fact]
    public void FileDescriptionDoesNotMatchWhenAbsent()
        => Assert.False(new FileDescriptionMatcher("video").Matches(Identity(description: null)));

    [Fact]
    public void RenamedExecutableIsStillCaughtByPublisher()
    {
        // The scenario the ETW backstop exists for: the user renames the exe to dodge IFEO.
        var renamed = Identity(image: "totally-not-a-downloader.exe", publisher: "CN=Open Media LLC");

        Assert.False(new ImageNameMatcher("4kvideodownloaderplus.exe").Matches(renamed));
        Assert.True(new PublisherMatcher("Open Media LLC").Matches(renamed));
    }
}
