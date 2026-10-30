using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using SlimFaasMcp.Models;
using SlimFaasMcp.Services;
using Xunit;

namespace SlimFaasMcp.Tests.Services;

public class ToolProxyServiceMoreTests
{
    private readonly Mock<ISwaggerService> _swaggerMock = new();
    private readonly Mock<IHttpClientFactory> _factoryMock = new();
    private readonly TestHandler _handler = new();
    private readonly ToolProxyService _sut;

    public ToolProxyServiceMoreTests()
    {
        var client = new HttpClient(_handler);
        _factoryMock.Setup(f => f.CreateClient("InsecureHttpClient"))
                    .Returns(client);

        var logger = NullLogger<ToolProxyService>.Instance;
        _sut = new ToolProxyService(_swaggerMock.Object, _factoryMock.Object, logger);
    }

    private void SetupSwaggerAndEndpoints(IEnumerable<Endpoint> endpoints)
    {
        var dummyDoc = JsonDocument.Parse("{}");
        _swaggerMock.Setup(s => s.GetSwaggerAsync(It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<IDictionary<string, string>?>(),
                It.IsAny<ushort?>()))
                    .ReturnsAsync(dummyDoc);
        _swaggerMock.Setup(s => s.ParseEndpoints(dummyDoc))
                    .Returns(endpoints);
    }

    [Fact]
    public async Task GetToolsAsync_ReturnsMergedToolsWithOverrides()
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "getPets",
                Url = "/pets",
                Verb = "GET",
                Summary = "Get pets",
                Parameters = new List<Parameter>(),
                ContentType = "application/json"
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        var promptObj = new
        {
            activeTools = new[] { "getPets" },
            tools = new[]
            {
                new { name = "getPets", description = "My custom desc" }
            }
        };
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(promptObj)));

        var tools = await _sut.GetToolsAsync("https://swagger.example", null, null, b64, null);

        var tool = Assert.Single(tools);
        Assert.Equal("My custom desc", tool.Description);
    }

    [Fact]
    public async Task ExecuteToolAsync_Get_ReplacesPathAndQuery()
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "getPet",
                Url = "/pets/{id}",
                Verb = "GET",
                Parameters = new List<Parameter>
                {
                    new() { Name = "id",   In = "path",  Required = true },
                    new() { Name = "type", In = "query", Required = false }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        _handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{ \"ok\": true }", Encoding.UTF8, "application/json")
        };

        var input = JsonSerializer.Deserialize<JsonElement>("{\"id\":42,\"type\":\"cat\"}");

        var result = await _sut.ExecuteToolAsync("https://swagger.example", "getPet", input, "https://api.example");

        Assert.Equal("{ \"ok\": true }", result.Text);
        Assert.Equal("https://api.example/pets/42?type=cat", _handler.LastRequest?.RequestUri!.ToString());
        Assert.Equal(HttpMethod.Get, _handler.LastRequest?.Method);
    }

    [Fact]
    public async Task ExecuteToolAsync_Post_WithBody()
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "createPet",
                Url = "/pets",
                Verb = "POST",
                Parameters = new List<Parameter>
                {
                    new() { Name = "body", In = "body", Required = true }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        _handler.Response = new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("{ \"id\": 1 }", Encoding.UTF8, "application/json")
        };

        var input = JsonSerializer.Deserialize<JsonElement>("{\"body\":{\"name\":\"Milo\"}}");

        var result = await _sut.ExecuteToolAsync("https://swagger.example", "createPet", input, "https://api.example");

        Assert.Equal("{ \"id\": 1 }", result.Text);
        Assert.Equal(HttpMethod.Post, _handler.LastRequest?.Method);
        var body = _handler.LastBody;
        Assert.Equal("{\"name\":\"Milo\"}", body);
    }

    [Fact]
    public async Task ExecuteToolAsync_Put_WithPathQueryAndBody_SendsOnlyBody()
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "updatePet",
                Url = "/pets/{id}",
                Verb = "PUT",
                Parameters = new List<Parameter>
                {
                    new() { Name = "id",     In = "path",  Required = true },
                    new() { Name = "notify", In = "query", Required = false },
                    new() { Name = "body",   In = "body",  Required = true }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        var input = JsonSerializer.Deserialize<JsonElement>(
            "{\"id\":42,\"notify\":true,\"body\":{\"name\":\"Milo\"}}");

        await _sut.ExecuteToolAsync("https://swagger.example", "updatePet", input, "https://api.example");

        Assert.Equal(HttpMethod.Put, _handler.LastRequest?.Method);
        Assert.Equal("https://api.example/pets/42?notify=true", _handler.LastRequest?.RequestUri!.ToString());
        Assert.Equal("{\"name\":\"Milo\"}", _handler.LastBody);
    }

    [Fact]
    public async Task ExecuteToolAsync_Post_WithNamedSwagger2BodyParameter_SendsItsValue()
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "addPet",
                Url = "/pets",
                Verb = "POST",
                Parameters = new List<Parameter>
                {
                    new() { Name = "pet", In = "body", Required = true }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        var input = JsonSerializer.Deserialize<JsonElement>("{\"pet\":{\"name\":\"Milo\"}}");

        await _sut.ExecuteToolAsync("https://swagger.example", "addPet", input, "https://api.example");

        Assert.Equal("{\"name\":\"Milo\"}", _handler.LastBody);
    }

    [Fact]
    public async Task ExecuteToolAsync_Post_WithoutBodyArgument_SendsArgumentsNotBoundElsewhere()
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "createPetInStore",
                Url = "/stores/{storeId}/pets",
                Verb = "POST",
                Parameters = new List<Parameter>
                {
                    new() { Name = "storeId", In = "path", Required = true },
                    new() { Name = "body",    In = "body", Required = true }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        var input = JsonSerializer.Deserialize<JsonElement>("{\"storeId\":\"s1\",\"name\":\"Milo\"}");

        await _sut.ExecuteToolAsync("https://swagger.example", "createPetInStore", input, "https://api.example");

        Assert.Equal("https://api.example/stores/s1/pets", _handler.LastRequest?.RequestUri!.ToString());
        Assert.Equal("{\"name\":\"Milo\"}", _handler.LastBody);
    }

    [Fact]
    public async Task ExecuteToolAsync_ToolNotFound_ReturnsError()
    {
        SetupSwaggerAndEndpoints([]);
        var input = JsonSerializer.Deserialize<JsonElement>("{}");

        var res = await _sut.ExecuteToolAsync("https://swagger.example", "unknown", input, "https://api.example");

        Assert.Contains("Tool not found", res.Text);
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("DELETE")]
    public async Task ExecuteToolAsync_SendsHeaderParameters(string verb)
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "pet",
                Url = "/pets/{id}",
                Verb = verb,
                Parameters = new List<Parameter>
                {
                    new() { Name = "id",            In = "path",   Required = true },
                    new() { Name = "X-Tenant-Id",   In = "header", Required = true },
                    new() { Name = "X-Max-Results", In = "header", Required = false },
                    new() { Name = "X-Trace",       In = "header", Required = false }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        var input = JsonSerializer.Deserialize<JsonElement>(
            "{\"id\":42,\"X-Tenant-Id\":\"acme\",\"X-Max-Results\":10,\"X-Trace\":null}");

        await _sut.ExecuteToolAsync("https://swagger.example", "pet", input, "https://api.example");

        var headers = _handler.LastRequest!.Headers;
        Assert.Equal("https://api.example/pets/42", _handler.LastRequest.RequestUri!.ToString());
        Assert.Equal(["acme"], headers.GetValues("X-Tenant-Id"));
        Assert.Equal(["10"], headers.GetValues("X-Max-Results"));
        Assert.False(headers.Contains("X-Trace"));
    }

    [Fact]
    public async Task ExecuteToolAsync_Post_SendsContentHeaderParameterWithBody()
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "createPet",
                Url = "/pets",
                Verb = "POST",
                Parameters = new List<Parameter>
                {
                    new() { Name = "Content-Language", In = "header", Required = false },
                    new() { Name = "body",             In = "body",   Required = true }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        var input = JsonSerializer.Deserialize<JsonElement>("{\"Content-Language\":\"fr\",\"body\":{\"name\":\"Milo\"}}");

        await _sut.ExecuteToolAsync("https://swagger.example", "createPet", input, "https://api.example");

        Assert.Equal(["fr"], _handler.LastRequest!.Content!.Headers.ContentLanguage);
    }

    [Fact]
    public async Task ExecuteToolAsync_ForwardedHeadersTakePrecedenceOverHeaderParameters()
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "getPets",
                Url = "/pets",
                Verb = "GET",
                Parameters = new List<Parameter>
                {
                    new() { Name = "Authorization", In = "header", Required = false },
                    new() { Name = "dpop",          In = "header", Required = false }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        var input = JsonSerializer.Deserialize<JsonElement>(
            "{\"Authorization\":\"Bearer from-tool\",\"dpop\":\"from-tool\"}");
        var forwarded = new Dictionary<string, string> { ["Dpop"] = "forwarded" };

        await _sut.ExecuteToolAsync("https://swagger.example", "getPets", input, "https://api.example", forwarded);

        var headers = _handler.LastRequest!.Headers;
        Assert.Null(headers.Authorization);
        Assert.Equal(["forwarded"], headers.GetValues("Dpop"));
    }

    [Fact]
    public async Task ExecuteToolAsync_RejectsHeaderParameterWithLineBreak()
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "getPets",
                Url = "/pets",
                Verb = "GET",
                Parameters = new List<Parameter>
                {
                    new() { Name = "X-Tenant-Id", In = "header", Required = true }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        var input = JsonSerializer.Deserialize<JsonElement>("{\"X-Tenant-Id\":\"acme\\r\\nX-Admin: true\"}");

        var result = await _sut.ExecuteToolAsync("https://swagger.example", "getPets", input, "https://api.example");

        Assert.Equal(400, result.StatusCode);
        using var error = JsonDocument.Parse(result.Text!);
        Assert.Equal("Header parameter X-Tenant-Id cannot contain CR, LF or NUL characters",
            error.RootElement.GetProperty("error").GetString());
        Assert.Null(_handler.LastRequest);
    }

    private class TestHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);

        /// <summary>Body captured at send time: the proxy disposes the request once it has been sent.</summary>
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return Response;
        }
    }
}
