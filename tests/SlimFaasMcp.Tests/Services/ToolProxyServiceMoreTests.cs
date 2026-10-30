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

    [Theory]
    [InlineData("\"a b/c?d#e\"", "a%20b%2Fc%3Fd%23e")]
    [InlineData("\"../admin\"", "..%2Fadmin")]
    [InlineData("\"café\"", "caf%C3%A9")]
    [InlineData("true", "true")]
    public async Task ExecuteToolAsync_Get_EncodesPathParameterAsOneSegment(string id, string expectedSegment)
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "getPetToys",
                Url = "/pets/{id}/toys",
                Verb = "GET",
                Parameters = new List<Parameter>
                {
                    new() { Name = "id", In = "path", Required = true }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        var input = JsonSerializer.Deserialize<JsonElement>($"{{\"id\":{id}}}");

        await _sut.ExecuteToolAsync("https://swagger.example", "getPetToys", input, "https://api.example");

        Assert.Equal($"https://api.example/pets/{expectedSegment}/toys", _handler.LastRequest?.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(".")]
    [InlineData("..")]
    public async Task ExecuteToolAsync_RejectsDotSegmentPathParameter(string id)
    {
        var endpoints = new List<Endpoint>
        {
            new()
            {
                Name = "deletePetToys",
                Url = "/pets/{id}/toys",
                Verb = "DELETE",
                Parameters = new List<Parameter>
                {
                    new() { Name = "id", In = "path", Required = true }
                }
            }
        };
        SetupSwaggerAndEndpoints(endpoints);

        var input = JsonSerializer.Deserialize<JsonElement>($"{{\"id\":\"{id}\"}}");

        var result = await _sut.ExecuteToolAsync("https://swagger.example", "deletePetToys", input, "https://api.example");

        Assert.Equal(400, result.StatusCode);
        using var error = JsonDocument.Parse(result.Text!);
        Assert.Equal("Path parameter id cannot be a dot segment (. or ..)", error.RootElement.GetProperty("error").GetString());
        Assert.Null(_handler.LastRequest);
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
