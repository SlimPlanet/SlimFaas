namespace SlimFaas.Tests;

public class TinyGuidTests
{
    [Fact]
    public async Task TiniyGuidShould()
    {
        var guid5 = TinyGuid.NewTinyGuid(5);
        Assert.Equal(5, guid5.Length);

        var guid3 = TinyGuid.NewTinyGuid(3);
        Assert.Equal(3, guid3.Length);

        var guid10 = TinyGuid.NewTinyGuid(10);
        Assert.Equal(10, guid10.Length);
    }
}
