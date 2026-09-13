using Wolfstare.Service.Api;

namespace Wolfstare.Service.Tests.Api;

public class UnlockChallengeImageTests
{
    [Fact]
    public void RendersAValidPng()
    {
        var png = UnlockChallengeImage.RenderPng(new string('a', 5000));

        Assert.True(png.Length > 100);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
    }

    [Fact]
    public void HandlesAShortStringWithoutThrowing()
    {
        var png = UnlockChallengeImage.RenderPng("abc");

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, png[..4]);
    }

    [Fact]
    public void DifferentTextProducesDifferentPixels()
    {
        // Sanity: the render actually reflects the text, so it isn't a blank placeholder.
        var a = UnlockChallengeImage.RenderPng(new string('a', 400));
        var b = UnlockChallengeImage.RenderPng(new string('b', 400));

        Assert.NotEqual(a, b);
    }
}
