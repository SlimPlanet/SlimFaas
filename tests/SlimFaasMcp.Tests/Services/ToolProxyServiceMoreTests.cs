using System.Net;
using System.Text;
using System.Text.Json;
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

        _sut = new ToolProxyService(_swaggerMock.Object, _factoryMock.Object);
    }

    private void SetupSwaggerAndEndpoints(IEnumerable<Endpoint> endpoints)
    {
        var dummyDoc = JsonDocument.Parse("{}");
        _swaggerMock.Setup(s => s.GetSwaggerAsync(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>()))
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

        var tools = await _sut.GetToolsAsync("https://swagger.example", null, null, b64);

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

        Assert.Equal("{ \"ok\": true }", result);
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

        Assert.Equal("{ \"id\": 1 }", result);
        Assert.Equal(HttpMethod.Post, _handler.LastRequest?.Method);
        var body = await _handler.LastRequest!.Content!.ReadAsStringAsync();
        Assert.Equal("{\"name\":\"Milo\"}", body);
    }

    [Fact]
    public async Task ExecuteToolAsync_ToolNotFound_ReturnsError()
    {
        SetupSwaggerAndEndpoints([]);
        var input = JsonSerializer.Deserialize<JsonElement>("{}");

        var res = await _sut.ExecuteToolAsync("https://swagger.example", "unknown", input, "https://api.example");

        Assert.Contains("Tool not found", res);
    }

    private class TestHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Response);
        }
    }
}
