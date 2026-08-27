namespace Wolfstare.Core.Tests;

public class HarnessTests
{
    [Fact]
    public void CoreAssemblyIsReferencable()
    {
        var assembly = typeof(Core.CoreMarker).Assembly;
        Assert.Equal("Wolfstare.Core", assembly.GetName().Name);
    }
}
