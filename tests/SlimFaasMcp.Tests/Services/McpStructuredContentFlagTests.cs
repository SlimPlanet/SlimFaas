using SlimFaasMcp.Models;
using SlimFaasMcp.Services;
using System.Text.Json.Nodes;
using Xunit;

namespace SlimFaasMcp.Tests;

public class McpStructuredContentFlagTests
{
    [Fact]
    public void StructuredContent_Disabled_When_Flag_False()
    {
        var r = new ProxyCallResult
        {
            IsBinary = false,
            MimeType = "application/json",
            Text = "{\"x\":1}"
        };

        var result = McpContentBuilder.BuildResult(r, enableStructuredContent: false);

        Assert.Null(result["structuredContent"]);
        var content = Assert.IsType<JsonArray>(result["content"]);
        var first = Assert.IsType<JsonObject>(content[0]!);
        Assert.Equal("text", (string?)first["type"]);
        Assert.Equal("{\"x\":1}", (string?)first["text"]);
    }

    [Fact]
    public void StructuredContent_Enabled_When_Flag_True()
    {
        var r = new ProxyCallResult
        {
            IsBinary = false,
            MimeType = "application/json",
            Text = "{\"x\":1}"
        };

        var result = McpContentBuilder.BuildResult(r, enableStructuredContent: true);

        var sc = Assert.IsType<JsonObject>(result["structuredContent"]);
        Assert.Equal(1, (int?)sc["x"]);

        var content = Assert.IsType<JsonArray>(result["content"]);
        var first = Assert.IsType<JsonObject>(content[0]!);
        Assert.Equal("text", (string?)first["type"]);
        Assert.Equal("{\"x\":1}", (string?)first["text"]);
    }
}
