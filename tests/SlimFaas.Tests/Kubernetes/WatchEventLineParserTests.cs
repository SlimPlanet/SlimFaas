using SlimFaas.Kubernetes.Watch;

namespace SlimFaas.Tests.Kubernetes;

public class WatchEventLineParserTests
{
    [Theory]
    [InlineData("ADDED")]
    [InlineData("MODIFIED")]
    [InlineData("DELETED")]
    public void ChangeEventsAreParsedWithResourceVersion(string type)
    {
        var info = WatchEventLineParser.Parse(
            $"{{\"type\":\"{type}\",\"object\":{{\"metadata\":{{\"name\":\"pod-1\",\"resourceVersion\":\"1234\"}}}}}}");

        Assert.Equal(WatchEventKind.Change, info.Kind);
        Assert.Equal("1234", info.ResourceVersion);
        Assert.Null(info.ErrorCode);
    }

    [Fact]
    public void BookmarkIsParsedWithResourceVersion()
    {
        var info = WatchEventLineParser.Parse(
            "{\"type\":\"BOOKMARK\",\"object\":{\"kind\":\"Pod\",\"metadata\":{\"resourceVersion\":\"5678\"}}}");

        Assert.Equal(WatchEventKind.Bookmark, info.Kind);
        Assert.Equal("5678", info.ResourceVersion);
    }

    [Fact]
    public void ErrorEventExposesStatusCode()
    {
        var info = WatchEventLineParser.Parse(
            "{\"type\":\"ERROR\",\"object\":{\"kind\":\"Status\",\"code\":410,\"message\":\"too old resource version\"}}");

        Assert.Equal(WatchEventKind.Error, info.Kind);
        Assert.Equal(410, info.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not json at all")]
    [InlineData("{\"unexpected\":true}")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"type\":\"SOMETHING_ELSE\",\"object\":{}}")]
    public void MalformedOrUnknownLinesNeverThrow(string line)
    {
        var info = WatchEventLineParser.Parse(line);

        Assert.Equal(WatchEventKind.Unknown, info.Kind);
    }

    [Fact]
    public void NestedMetadataDoesNotConfuseTheParser()
    {
        // resourceVersion imbriquée dans un champ non-metadata : ne doit pas être retenue.
        var info = WatchEventLineParser.Parse(
            "{\"type\":\"MODIFIED\",\"object\":{\"spec\":{\"template\":{\"metadata\":{\"resourceVersion\":\"999\"}}},\"metadata\":{\"resourceVersion\":\"42\"}}}");

        Assert.Equal(WatchEventKind.Change, info.Kind);
        Assert.Equal("42", info.ResourceVersion);
    }
}
