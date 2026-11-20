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

    private const string PetsOperation = """
        "paths": { "/pets": { "get": { "summary": "List pets" } } }
        """;

    // Without base_url, the API is called at the base URL described by the document.
    [Theory]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "https://other.example.com/base" } ], """ + PetsOperation + "}",
        "https://other.example.com/base/pets")]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "/v1" } ], """ + PetsOperation + "}",
        "https://api.example.com/v1/pets")]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "v1" } ], """ + PetsOperation + "}",
        "https://api.example.com/docs/v1/pets")]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "https://{env}.example.com/{version}", "variables": { "env": { "default": "pets" }, "version": { "default": "v2" } } } ], """ + PetsOperation + "}",
        "https://pets.example.com/v2/pets")]
    [InlineData("""{ "openapi": "3.0.0", """ + PetsOperation + "}",
        "https://api.example.com/pets")]
    [InlineData("""{ "swagger": "2.0", "host": "pets.example.com", "basePath": "/api", "schemes": ["http"], """ + PetsOperation + "}",
        "http://pets.example.com/api/pets")]
    [InlineData("""{ "swagger": "2.0", "host": "pets.example.com", """ + PetsOperation + "}",
        "https://pets.example.com/pets")]
    [InlineData("""{ "swagger": "2.0", "basePath": "/api", """ + PetsOperation + "}",
        "https://api.example.com/api/pets")]
    [InlineData("""{ "swagger": "2.0", "basePath": "/api", "schemes": ["http"], """ + PetsOperation + "}",
        "http://api.example.com/api/pets")]
    [InlineData("""{ "swagger": "2.0", "schemes": ["http"], """ + PetsOperation + "}",
        "http://api.example.com/pets")]
    public async Task ExecuteToolAsync_Without_BaseUrl_Calls_The_Document_Base_Url(string swaggerJson, string expectedUrl)
    {
        var (svc, requestedUrls) = CreateWithDocument(swaggerJson);
        using var argsDoc = JsonDocument.Parse("{}");

        // The MCP endpoint passes an empty base_url when the query string has none.
        foreach (var baseUrl in new[] { null, "" })
        {
            var result = await svc.ExecuteToolAsync(DocumentUrl, "get_pets", argsDoc.RootElement, baseUrl);

            Assert.Equal(200, result.StatusCode);
            Assert.Equal(expectedUrl, requestedUrls[^1]);
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_BaseUrl_Takes_Precedence_Over_The_Document_Servers()
    {
        var (svc, requestedUrls) = CreateWithDocument(
            """{ "openapi": "3.0.0", "servers": [ { "url": "https://other.example.com" } ], """ + PetsOperation + "}");
        using var argsDoc = JsonDocument.Parse("{}");

        await svc.ExecuteToolAsync(DocumentUrl, "get_pets", argsDoc.RootElement, "https://pets.example.com/api");

        Assert.Equal("https://pets.example.com/api/pets", requestedUrls[^1]);
    }

    [Fact]
    public async Task ExecuteToolAsync_Without_BaseUrl_Uses_The_Document_Scheme_For_A_Swagger2_Host_Without_Schemes()
    {
        const string documentUrl = "http://api.example.com/docs/openapi.json";
        var (svc, requestedUrls) = CreateWithDocument(
            """{ "swagger": "2.0", "host": "pets.example.com", """ + PetsOperation + "}", documentUrl);
        using var argsDoc = JsonDocument.Parse("{}");

        await svc.ExecuteToolAsync(documentUrl, "get_pets", argsDoc.RootElement);

        Assert.Equal("http://pets.example.com/pets", requestedUrls[^1]);
    }

    [Fact]
    public async Task ExecuteToolAsync_Without_BaseUrl_Uses_The_Declared_Scheme_For_A_Swagger2_Document_Without_Host()
    {
        const string documentUrl = "http://api.example.com:8080/docs/openapi.json";
        var (svc, requestedUrls) = CreateWithDocument(
            """{ "swagger": "2.0", "basePath": "/api", "schemes": ["https"], """ + PetsOperation + "}", documentUrl);
        using var argsDoc = JsonDocument.Parse("{}");

        await svc.ExecuteToolAsync(documentUrl, "get_pets", argsDoc.RootElement);

        Assert.Equal("https://api.example.com:8080/api/pets", requestedUrls[^1]);
    }

    [Theory]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "https://{env}.example.com" } ], """ + PetsOperation + "}")]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "/api/{version}", "variables": { "version": { } } } ], """ + PetsOperation + "}")]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "https://pets.example.com/api/{version}" } ], """ + PetsOperation + "}")]
    public async Task ExecuteToolAsync_Without_BaseUrl_Rejects_A_Server_Url_With_An_Undefined_Variable(string swaggerJson)
    {
        var (svc, requestedUrls) = CreateWithDocument(swaggerJson);
        using var argsDoc = JsonDocument.Parse("{}");

        await Assert.ThrowsAsync<ArgumentException>(() =>
            svc.ExecuteToolAsync(DocumentUrl, "get_pets", argsDoc.RootElement));

        Assert.Equal([DocumentUrl], requestedUrls);
    }

    // The servers of an operation come first, then those of its path item, then those of the document.
    [Theory]
    [InlineData("""{ "openapi": "3.0.0", "paths": { "/pets": { "get": { "servers": [ { "url": "/animal-api" } ] } } } }""",
        "https://api.example.com/animal-api/pets")]
    [InlineData("""{ "openapi": "3.0.0", "paths": { "/pets": { "servers": [ { "url": "/animal-api" } ], "get": { } } } }""",
        "https://api.example.com/animal-api/pets")]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "/root" } ], "paths": { "/pets": { "servers": [ { "url": "/animal-api" } ], "get": { } } } }""",
        "https://api.example.com/animal-api/pets")]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "/root" } ], "paths": { "/pets": { "servers": [ { "url": "/path" } ], "get": { "servers": [ { "url": "/operation" } ] } } } }""",
        "https://api.example.com/operation/pets")]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "/root" } ], "paths": { "/pets": { "get": { "servers": [ ] } } } }""",
        "https://api.example.com/root/pets")]
    [InlineData("""{ "openapi": "3.0.0", "servers": [ { "url": "/root" } ], "paths": { "/pets": { "get": { }, "post": { "servers": [ { "url": "/other" } ] } } } }""",
        "https://api.example.com/root/pets")]
    public async Task ExecuteToolAsync_Without_BaseUrl_Uses_The_Servers_Of_The_Operation(string swaggerJson, string expectedUrl)
    {
        var (svc, requestedUrls) = CreateWithDocument(swaggerJson);
        using var argsDoc = JsonDocument.Parse("{}");

        var result = await svc.ExecuteToolAsync(DocumentUrl, "get_pets", argsDoc.RootElement);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(expectedUrl, requestedUrls[^1]);
    }

    // The schemes of a Swagger 2.0 operation override those of the document, which override the scheme of the document URL.
    [Theory]
    [InlineData("""{ "swagger": "2.0", "basePath": "/api", "schemes": ["http"], "paths": { "/pets": { "get": { "schemes": ["https"] } } } }""",
        "https://api.example.com/api/pets")]
    [InlineData("""{ "swagger": "2.0", "basePath": "/api", "paths": { "/pets": { "get": { "schemes": ["https"] } } } }""",
        "https://api.example.com/api/pets")]
    [InlineData("""{ "swagger": "2.0", "host": "pets.example.com", "schemes": ["http"], "paths": { "/pets": { "get": { "schemes": ["https"] } } } }""",
        "https://pets.example.com/pets")]
    [InlineData("""{ "swagger": "2.0", "basePath": "/api", "schemes": ["https"], "paths": { "/pets": { "get": { "schemes": ["http"] } } } }""",
        "http://api.example.com/api/pets")]
    [InlineData("""{ "swagger": "2.0", "basePath": "/api", "schemes": ["https"], "paths": { "/pets": { "get": { "schemes": [] } } } }""",
        "https://api.example.com/api/pets")]
    public async Task ExecuteToolAsync_Without_BaseUrl_Uses_The_Schemes_Of_The_Swagger2_Operation(string swaggerJson, string expectedUrl)
    {
        const string documentUrl = "http://api.example.com/docs/openapi.json";
        var (svc, requestedUrls) = CreateWithDocument(swaggerJson, documentUrl);
        using var argsDoc = JsonDocument.Parse("{}");

        // The second call is served from the cache.
        for (var i = 0; i < 2; i++)
        {
            var result = await svc.ExecuteToolAsync(documentUrl, "get_pets", argsDoc.RootElement);

            Assert.Equal(200, result.StatusCode);
            Assert.Equal(expectedUrl, requestedUrls[^1]);
        }
    }

    [Fact]
    public async Task ExecuteToolAsync_Without_BaseUrl_Resolves_A_Relative_Server_Url_Against_The_Redirected_Document_Url()
    {
        const string inputUrl = "https://api.example.com/latest";
        const string finalUrl = "https://api.example.com/releases/current/openapi.json";
        var requestedUrls = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            requestedUrls.Add(url);
            if (url == inputUrl)
            {
                // What HttpClient reports once it has followed a redirect: the request that got the final response.
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{ "openapi": "3.0.0", "servers": [ { "url": "v1" } ], """ + PetsOperation + "}",
                        Encoding.UTF8, "application/json"),
                    RequestMessage = new HttpRequestMessage(HttpMethod.Get, finalUrl)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
                   { Content = new StringContent("""{ "pets": [] }""", Encoding.UTF8, "application/json") };
        });
        var factory = new FakeHttpClientFactory(stub);
        var swagger = new SwaggerService(factory, new MemoryCache(new MemoryCacheOptions()));
        var svc = new ToolProxyService(swagger, factory, NullLogger<ToolProxyService>.Instance);
        using var argsDoc = JsonDocument.Parse("{}");

        // The second call reads the document from the cache.
        for (var i = 0; i < 2; i++)
        {
            var result = await svc.ExecuteToolAsync(inputUrl, "get_pets", argsDoc.RootElement);

            Assert.Equal(200, result.StatusCode);
            Assert.Equal("https://api.example.com/releases/current/v1/pets", requestedUrls[^1]);
        }

        Assert.Equal(1, requestedUrls.Count(u => u == inputUrl));
    }

    private const string DocumentUrl = "https://api.example.com/docs/openapi.json";

    private static (ToolProxyService svc, List<string> requestedUrls) CreateWithDocument(string swaggerJson,
        string documentUrl = DocumentUrl)
    {
        var requestedUrls = new List<string>();
        var stub = new StubHttpMessageHandler(req =>
        {
            var url = req.RequestUri!.AbsoluteUri;
            requestedUrls.Add(url);
            return url == documentUrl
                ? new HttpResponseMessage(HttpStatusCode.OK)
                  { Content = new StringContent(swaggerJson, Encoding.UTF8, "application/json") }
                : new HttpResponseMessage(HttpStatusCode.OK)
                  { Content = new StringContent("""{ "pets": [] }""", Encoding.UTF8, "application/json") };
        });
        var factory = new FakeHttpClientFactory(stub);
        var swagger = new SwaggerService(factory, new MemoryCache(new MemoryCacheOptions()));
        return (new ToolProxyService(swagger, factory, NullLogger<ToolProxyService>.Instance), requestedUrls);
    }
}
