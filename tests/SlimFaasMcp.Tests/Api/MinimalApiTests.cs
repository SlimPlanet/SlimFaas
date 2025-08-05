// Tests/Api/MinimalApiTests.cs
using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using SlimFaasMcp.Models;
using SlimFaasMcp.Services;
using Xunit;

public class MinimalApiTests : IClassFixture<MinimalApiTests.TestAppFactory>
{
    private readonly HttpClient _client;

    public MinimalApiTests(TestAppFactory factory) => _client = factory.CreateClient();

    [Fact]
    public async Task Health_Endpoint_Returns_OK()
    {
        var txt = await _client.GetStringAsync("/health");
        Assert.Equal("OK", txt);
    }

    [Fact]
    public async Task Tools_List_Returns_Stubbed_Tools()
    {
        var tools = await _client.GetFromJsonAsync<List<McpTool>>(
            "/tools?openapi_url=https://dummy");

        var tool = Assert.Single(tools!);
        Assert.Equal("dummy", tool.Name);
    }

    [Fact]
    public async Task Tool_Call_Returns_Stubbed_Response()
    {
        var res = await _client.PostAsJsonAsync(
            "/tools/dummy?openapi_url=https://dummy",
            new { foo = "bar" });

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var body = await res.Content.ReadAsStringAsync();
        Assert.Equal(@"""{\""status\"":\""ok\""}""", body);
    }

    // ------------------------------------------------------------------
    public sealed class TestAppFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureServices(services =>
            {
                // remplace le vrai service par un stub
                services.AddSingleton<IToolProxyService>(new FakeToolProxyService());
            });
        }
    }

    private sealed class FakeToolProxyService : IToolProxyService
    {
        private static readonly List<McpTool> _tools =
        [
            new McpTool
            {
                Name = "dummy",
                Description = "stub",
                InputSchema = new System.Text.Json.Nodes.JsonObject(),
                Endpoint = new McpTool.EndpointInfo{ Url="/dummy", Method="GET", ContentType="application/json" }
            }
        ];

        public Task<List<McpTool>> GetToolsAsync(string s1,string? s2,IDictionary<string,string> s3,string? s4)
            => Task.FromResult(_tools);

        public Task<string> ExecuteToolAsync(string s1,string s2,
                                             System.Text.Json.JsonElement e,string? s3,IDictionary<string,string>? s4)
            => Task.FromResult(@"{""status"":""ok""}");
    }
}
