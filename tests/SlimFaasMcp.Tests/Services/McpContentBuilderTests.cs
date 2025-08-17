using System.Text;
using System.Text.Json.Nodes;
using SlimFaasMcp.Models;
using SlimFaasMcp.Services;
using Xunit;

namespace SlimFaasMcp.Tests;

public class McpContentBuilderTests
{
    [Fact]
    public void Build_Image_Yields_McpImage()
    {
        var bytes = Encoding.UTF8.GetBytes("img");
        var r = new ProxyCallResult
        {
            IsBinary = true,
            MimeType = "image/png",
            FileName = "a.png",
            Bytes    = bytes
        };

        JsonArray content = McpContentBuilder.Build(r);

        Assert.Single(content);
        var o = Assert.IsType<JsonObject>(content[0]!);
        Assert.Equal("image", (string?)o["type"]);
        Assert.Equal("image/png", (string?)o["mimeType"]);
        var b64 = (string?)o["data"];
        Assert.NotNull(b64);
        Assert.Equal(Convert.ToBase64String(bytes), b64);
    }

    [Fact]
    public void Build_Audio_Yields_McpAudio()
    {
        var bytes = Encoding.UTF8.GetBytes("aud");
        var r = new ProxyCallResult
        {
            IsBinary = true,
            MimeType = "audio/mpeg",
            FileName = "a.mp3",
            Bytes    = bytes
        };

        var content = McpContentBuilder.Build(r);

        Assert.Single(content);
        var o = Assert.IsType<JsonObject>(content[0]!);
        Assert.Equal("audio", (string?)o["type"]);
        Assert.Equal("audio/mpeg", (string?)o["mimeType"]);
        Assert.Equal(Convert.ToBase64String(bytes), (string?)o["data"]);
    }

    [Fact]
    public void Build_GenericBinary_Yields_McpResource()
    {
        var bytes = Encoding.UTF8.GetBytes("pdf");
        var r = new ProxyCallResult
        {
            IsBinary = true,
            MimeType = "application/pdf",
            FileName = "doc.pdf",
            Bytes    = bytes
        };

        var content = McpContentBuilder.Build(r);

        Assert.Single(content);
        var o = Assert.IsType<JsonObject>(content[0]!);
        Assert.Equal("resource", (string?)o["type"]);

        var res = Assert.IsType<JsonObject>(o["resource"]!);
        Assert.StartsWith("slimfaas://tool-result/", (string?)res["uri"]);
        Assert.Equal("doc.pdf", (string?)res["name"]);
        Assert.Equal("application/pdf", (string?)res["mimeType"]);
        Assert.Equal(bytes.Length, (int?)res["size"]);
        Assert.Equal(Convert.ToBase64String(bytes), (string?)res["blob"]);
    }

    [Fact]
    public void Build_Text_Yields_McpText()
    {
        var r = new ProxyCallResult
        {
            IsBinary = false,
            MimeType = "application/json",
            Text     = "{\"ok\":true}"
        };

        var content = McpContentBuilder.Build(r);

        Assert.Single(content);
        var o = Assert.IsType<JsonObject>(content[0]!);
        Assert.Equal("text", (string?)o["type"]);
        Assert.Equal("{\"ok\":true}", (string?)o["text"]);
    }
}
