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
    public void WrapNeverSplitsAWordAcrossLines()
    {
        var words = Enumerable.Range(0, 60).Select(i => $"word{i}").ToArray();
        var text = string.Join(' ', words);

        var lines = UnlockChallengeImage.Wrap(text, 80);

        // Every token on every line is one of the original whole words — nothing was cut.
        var wordSet = words.ToHashSet();
        foreach (var line in lines)
        {
            Assert.True(line.Length <= 80, $"line too wide: {line.Length}");
            foreach (var token in line.Split(' '))
                Assert.Contains(token, wordSet);
        }

        // And the words, read back in order across lines, are the original sequence intact.
        Assert.Equal(words, lines.SelectMany(l => l.Split(' ')).ToArray());
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
