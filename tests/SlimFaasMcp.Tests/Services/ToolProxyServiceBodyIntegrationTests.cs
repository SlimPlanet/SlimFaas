using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using SlimFaasMcp.Services;
using Xunit;

namespace SlimFaasMcp.Tests.Services;

public class ToolProxyServiceBodyIntegrationTests
{
    private const string BaseUrl = "https://api.example.com";
    private const string SwaggerUrl = BaseUrl + "/openapi.json";
    private const string QuestionSchema = """
        {
          "type": "object",
          "required": ["question"],
          "properties": {
            "question": { "type": "string", "description": "The user's question" }
          }
        }
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Single_Property_Json_Body_Should_Keep_Object_Schema_And_Http_Payload(bool useReference)
    {
        string schema = useReference ? """{"$ref":"#/components/schemas/QuestionRequest"}""" : QuestionSchema;
        using var fixture = new BodyFixture(schema, "application/json");
        await fixture.InitializeAsync();

        var tools = await fixture.Proxy.GetToolsAsync(SwaggerUrl, BaseUrl, null, null, null);

        var tool = Assert.Single(tools);
        Assert.Equal("post_ask", tool.Name);
        Assert.Equal("body", Assert.Single(tool.InputSchema["required"]!.AsArray())!.GetValue<string>());
        var bodySchema = tool.InputSchema["properties"]!["body"]!;
        Assert.Equal("object", bodySchema["type"]!.GetValue<string>());
        Assert.Equal("question", Assert.Single(bodySchema["required"]!.AsArray())!.GetValue<string>());
        Assert.Equal("string", bodySchema["properties"]!["question"]!["type"]!.GetValue<string>());
        Assert.Equal("The user's question", bodySchema["properties"]!["question"]!["description"]!.GetValue<string>());
        using var arguments = JsonDocument.Parse("""{"body":{"question":"What is SlimFaas?"}}""");

        var result = await fixture.Proxy.ExecuteToolAsync(SwaggerUrl, tool.Name!, arguments.RootElement, BaseUrl);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(HttpMethod.Post, fixture.Handler.Method);
        Assert.Equal(new Uri(BaseUrl + "/ask"), fixture.Handler.RequestUri);
        Assert.Equal("application/json", fixture.Handler.MediaType);
        Assert.Equal("""{"question":"What is SlimFaas?"}""", fixture.Handler.Body);
    }

    [Fact]
    public async Task Scalar_Json_Body_Should_Keep_String_Schema_And_Json_String_Payload()
    {
        using var fixture = new BodyFixture("""{"type":"string"}""", "application/json");
        await fixture.InitializeAsync();
        var tool = Assert.Single(await fixture.Proxy.GetToolsAsync(SwaggerUrl, BaseUrl, null, null, null));
        Assert.Equal("string", tool.InputSchema["properties"]!["body"]!["type"]!.GetValue<string>());
        using var arguments = JsonDocument.Parse("""{"body":"hello"}""");

        var result = await fixture.Proxy.ExecuteToolAsync(SwaggerUrl, tool.Name!, arguments.RootElement, BaseUrl);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("application/json", fixture.Handler.MediaType);
        Assert.Equal("\"hello\"", fixture.Handler.Body);
    }

    [Fact]
    public async Task Multipart_Body_Should_Keep_Flat_Arguments_And_Named_Form_Parts()
    {
        const string schema = """
            {
              "type": "object",
              "required": ["question"],
              "properties": {
                "question": { "type": "string" },
                "tag": { "type": "string" }
              }
            }
            """;
        using var fixture = new BodyFixture(schema, "multipart/form-data");
        await fixture.InitializeAsync();
        var tool = Assert.Single(await fixture.Proxy.GetToolsAsync(SwaggerUrl, BaseUrl, null, null, null));
        var properties = tool.InputSchema["properties"]!.AsObject();
        Assert.Equal(2, properties.Count);
        Assert.False(properties.ContainsKey("body"));
        Assert.Equal("string", properties["question"]!["type"]!.GetValue<string>());
        Assert.Equal("string", properties["tag"]!["type"]!.GetValue<string>());
        Assert.Equal("question", Assert.Single(tool.InputSchema["required"]!.AsArray())!.GetValue<string>());
        using var arguments = JsonDocument.Parse("""{"question":"hello","tag":"example"}""");

        var result = await fixture.Proxy.ExecuteToolAsync(SwaggerUrl, tool.Name!, arguments.RootElement, BaseUrl);

        Assert.Equal(200, result.StatusCode);
        Assert.Equal("multipart/form-data", fixture.Handler.MediaType);
        Assert.Equal(2, fixture.Handler.FormFields.Count);
        Assert.Equal("hello", fixture.Handler.FormFields["question"]);
        Assert.Equal("example", fixture.Handler.FormFields["tag"]);
    }

    private sealed class BodyFixture : IDisposable
    {
        private readonly HttpClient _client;
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly SwaggerService _swagger;
        private JsonDocument? _document;

        public BodyFixture(string schema, string contentType)
        {
            string swagger = $$"""
                {
                  "openapi": "3.0.0",
                  "paths": { "/ask": { "post": {
                    "requestBody": {
                      "required": true,
                      "content": { "{{contentType}}": { "schema": {{schema}} } }
                    }
                  } } },
                  "components": { "schemas": { "QuestionRequest": {{QuestionSchema}} } }
                }
                """;
            Handler = new BodyHandler(swagger);
            _client = new HttpClient(Handler);
            var factory = new SingleClientFactory(_client);
            _swagger = new SwaggerService(factory, _cache);
            Proxy = new ToolProxyService(_swagger, factory, NullLogger<ToolProxyService>.Instance);
        }

        public BodyHandler Handler { get; }
        public ToolProxyService Proxy { get; }

        public async Task InitializeAsync()
        {
            // Own the cached document for both real service calls and dispose it with the fixture.
            _document = await _swagger.GetSwaggerAsync(SwaggerUrl);
        }

        public void Dispose()
        {
            _document?.Dispose();
            _cache.Dispose();
            _client.Dispose();
        }
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class BodyHandler(string swagger) : HttpMessageHandler
    {
        private readonly List<HttpResponseMessage> _responses = [];
        public HttpMethod? Method { get; private set; }
        public Uri? RequestUri { get; private set; }
        public string? MediaType { get; private set; }
        public string? Body { get; private set; }
        public Dictionary<string, string> FormFields { get; } = new(StringComparer.Ordinal);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            string responseBody = swagger;
            if (request.RequestUri?.AbsoluteUri != SwaggerUrl)
            {
                Method = request.Method;
                RequestUri = request.RequestUri;
                MediaType = request.Content?.Headers.ContentType?.MediaType;
                Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
                if (request.Content is MultipartFormDataContent multipart)
                {
                    foreach (var part in multipart)
                    {
                        string name = part.Headers.ContentDisposition!.Name!.Trim('"');
                        FormFields.Add(name, await part.ReadAsStringAsync(cancellationToken));
                    }
                }
                responseBody = """{"ok":true}""";
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            };
            _responses.Add(response);
            return response;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in _responses)
                {
                    response.Dispose();
                }
            }
            base.Dispose(disposing);
        }
    }
}
