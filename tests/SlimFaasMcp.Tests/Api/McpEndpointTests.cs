using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using SlimFaasMcp.Models;
using SlimFaasMcp.Services;
using Xunit;

namespace SlimFaasMcp.Tests.Api;

public class McpEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly Mock<IToolProxyService> _toolProxyMock = new();

    public McpEndpointTests()
    {
        _factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureServices(services =>
                {
                    // Remplace l'implémentation réelle par un mock Moq
                    var descriptor = services.Single(s => s.ServiceType == typeof(IToolProxyService));
                    services.Remove(descriptor);
                    services.AddSingleton(_toolProxyMock.Object);
                });
            });
    }

    [Fact]
    public async Task Initialize_ReturnsExpectedResult()
    {
        var req = new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "initialize"
        };

        var client   = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/mcp", req);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonNode>();
        Assert.Equal("2.0", json?["jsonrpc"]!.GetValue<string>());
        Assert.Equal(1, json?["id"]!.GetValue<int>());

        var result = json?["result"]?.AsObject();
        Assert.NotNull(result);
        Assert.Equal("2025-06-18", result!["protocolVersion"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolsList_ReturnsToolsFromProxy()
    {
        var tools = new List<McpTool>
        {
            new() { Name = "getPets", Description = "Get all pets", InputSchema = new JsonObject() }
        };
        _toolProxyMock.Setup(p => p.GetToolsAsync(It.IsAny<string>(), It.IsAny<string?>(),
                                                  It.IsAny<IDictionary<string,string>>(), It.IsAny<string?>()))
                      .ReturnsAsync(tools);

        var rpc = JsonSerializer.Deserialize<JsonNode>("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""")!;

        var client   = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/mcp?openapi_url=https://example.com/openapi.json", rpc);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonNode>();
        var returnedTools = json?["result"]?["tools"]?.AsArray();
        Assert.Single(returnedTools);
        Assert.Equal("getPets", returnedTools![0]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task ToolsCall_MissingParams_ReturnsError32602()
    {
        var rpc = JsonSerializer.Deserialize<JsonNode>("""{"jsonrpc":"2.0","id":3,"method":"tools/call"}""")!;

        var client   = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/mcp", rpc);
        response.EnsureSuccessStatusCode();

        var json  = await response.Content.ReadFromJsonAsync<JsonNode>();
        var error = json?["error"]!.AsObject();
        Assert.Equal(-32602, error["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task ToolsCall_HappyPath_WrapsProxyResponse()
    {
        ProxyCallResult proxyCallResult = new() { Text = """{ "pets": [] }""" };
        _toolProxyMock.Setup(p => p.ExecuteToolAsync(It.IsAny<string>(),
                                                     "getPets",
                                                     It.IsAny<JsonElement>(),
                                                     It.IsAny<string?>(),
                                                     It.IsAny<IDictionary<string,string>?>()))
                      .ReturnsAsync(proxyCallResult);

        var rpc = JsonSerializer.Deserialize<JsonNode>("""{"jsonrpc":"2.0","id":"abc","method":"tools/call","params":{"name":"getPets","arguments":{}}}""")!;

        var client   = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/mcp?openapi_url=https://example.com/openapi.json", rpc);
        response.EnsureSuccessStatusCode();

        var json    = await response.Content.ReadFromJsonAsync<JsonNode>();
        var content = json?["result"]?["content"]?.AsArray();
        Assert.Single(content);
        var first = content![0]!.AsObject();
        Assert.Equal("text", first["type"]!.GetValue<string>());
        Assert.Contains("\"pets\": []", first["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task UnknownMethod_ReturnsError32601()
    {
        var rpc = new
        {
            jsonrpc = "2.0",
            id      = 4,
            method  = "does/not/exist"
        };

        var client = _factory.CreateClient();
        var res    = await client.PostAsJsonAsync("/mcp", rpc);
        res.EnsureSuccessStatusCode();

        var json  = await res.Content.ReadFromJsonAsync<JsonNode>();
        var error = json?["error"]!.AsObject();
        Assert.Equal(-32601, error["code"]!.GetValue<int>());
    }
}
