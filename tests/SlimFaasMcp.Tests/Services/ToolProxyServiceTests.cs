// Tests/Services/ToolProxyServiceTests.cs
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SlimFaasMcp.Services;
using Xunit;

public class ToolProxyServiceTests
{
    private const string SwaggerJson = """
    {
      "openapi":"3.0.0",
      "paths":{ "/pets":{
        "get":{ "summary":"List pets",
                 "parameters":[{ "name":"limit","in":"query","schema":{"type":"integer"} }] } } }
    }
    """;

    private static (ToolProxyService svc, StubHttpMessageHandler stub) Create()
    {
        var stub = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;

            if (url.EndsWith("/openapi.json"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                       { Content = new StringContent(SwaggerJson, Encoding.UTF8, "application/json") };

            if (url.StartsWith("https://api.example.com/pets"))
                return new HttpResponseMessage(HttpStatusCode.OK)
                       { Content = new StringContent("""{ "pets": [] }""",
                                                      Encoding.UTF8, "application/json") };

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var factory = new FakeHttpClientFactory(stub);
        var swagger = new SwaggerService(factory, new MemoryCache(new MemoryCacheOptions()));
        var logger = NullLogger<ToolProxyService>.Instance;
        return (new ToolProxyService(swagger, factory, logger), stub);
    }

    [Fact]
    public async Task GetToolsAsync_Translates_Swagger_To_Tools()
    {
        var (svc, _) = Create();
        var tools = await svc.GetToolsAsync("https://api.example.com/openapi.json",
                                            "https://api.example.com", null, null, null);

        var tool = Assert.Single(tools);
        Assert.Equal("get_pets", tool.Name);
        Assert.Equal("List pets", tool.Description);
    }

    [Fact]
    public async Task ExecuteToolAsync_Returns_Remote_Response()
    {
        var (svc, stub) = Create();
        using var argsDoc = JsonDocument.Parse("""{ "limit":5 }""");

        var json = await svc.ExecuteToolAsync(
            "https://api.example.com/openapi.json",
            "get_pets",
            argsDoc.RootElement,
            "https://api.example.com", null);

        Assert.Equal("""{ "pets": [] }""", json.Text);
        Assert.Equal(2, stub.CallCount); // 1 appel swagger + 1 appel endpoint
    }
}
