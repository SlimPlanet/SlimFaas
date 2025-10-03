// Tests/Services/SwaggerServiceTests.cs
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SlimFaasMcp.Services;
using Xunit;

public class SwaggerServiceTests
{
    private const string SwaggerJson = """
                                           {
                                             "openapi":"3.0.0",
                                             "paths":{ "/pets":{
                                               "get":{ "summary":"List pets",
                                                        "parameters":[{ "name":"limit","in":"query","schema":{"type":"integer"} }] } } }
                                           }
                                        """;

    // Nouveau : version YAML du même contenu
    private const string SwaggerYaml = """
openapi: "3.0.0"
paths:
  /pets:
    get:
      summary: "List pets"
      parameters:
        - name: "limit"
          in: "query"
          schema:
            type: "integer"
""";

    private static (ISwaggerService svc, StubHttpMessageHandler stub) Create()
    {
        var stub = new StubHttpMessageHandler(req =>
        {
            var path = req.RequestUri!.AbsoluteUri;

            if (path.EndsWith("/openapi.json"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SwaggerJson, Encoding.UTF8, "application/json")
                };
            }
            else if (path.EndsWith("/openapi.yaml") || path.EndsWith("/openapi.yml"))
            {
                // On renvoie du YAML avec un content-type YAML
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(SwaggerYaml, Encoding.UTF8, "application/yaml")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        var factory = new FakeHttpClientFactory(stub);
        return (new SwaggerService(factory, new MemoryCache(new MemoryCacheOptions())), stub);
    }

    [Fact]
    public async Task GetSwagger_Is_Cached()
    {
        var (svc, stub) = Create();
        _ = await svc.GetSwaggerAsync("https://api.example.com/openapi.json");
        _ = await svc.GetSwaggerAsync("https://api.example.com/openapi.json");

        Assert.Equal(1, stub.CallCount);           // une seule requête HTTP => cache OK
    }

    [Fact]
    public async Task GetSwagger_Not_Cached()
    {
        var (svc, stub) = Create();
        _ = await svc.GetSwaggerAsync("https://api.example.com/openapi.json", null, null, 0);
        _ = await svc.GetSwaggerAsync("https://api.example.com/openapi.json", null, null, 0);

        Assert.Equal(2, stub.CallCount);           // no cache when expiration is 0 => two calls
    }

    [Fact]
    public async Task ParseEndpoints_Returns_Expected_Endpoint()
    {
        var (svc, _) = Create();
        var doc      = await svc.GetSwaggerAsync("https://api.example.com/openapi.json");

        var ep = svc.ParseEndpoints(doc).Single();
        Assert.Equal("get_pets", ep.Name);
        Assert.Equal("/pets",   ep.Url);
        Assert.Equal("GET",     ep.Verb);
    }

    // Nouveau : test du parsing YAML
    [Fact]
    public async Task GetSwagger_Yaml_Parses()
    {
        var (svc, stub) = Create();
        var doc = await svc.GetSwaggerAsync("https://api.example.com/openapi.yaml");

        var ep = svc.ParseEndpoints(doc).Single();
        Assert.Equal("get_pets", ep.Name);
        Assert.Equal("/pets",   ep.Url);
        Assert.Equal("GET",     ep.Verb);

        // Vérifie que le cache marche aussi pour YAML
        _ = await svc.GetSwaggerAsync("https://api.example.com/openapi.yaml");
        Assert.Equal(1, stub.CallCount);
    }
}
