// Tests/Services/SwaggerServiceTests.cs
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SlimFaasMcp.Models;
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

    [Fact]
    public void ParseEndpoints_Applies_Path_Item_Parameters_To_Each_Operation()
    {
        var (svc, _) = Create();
        using var doc = JsonDocument.Parse("""
            {
              "openapi": "3.0.0",
              "components": { "parameters": {
                "PetId": { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } }
              } },
              "paths": { "/pets/{id}": {
                "summary": "One pet",
                "description": "Operations on one pet",
                "x-owner": "pets-team",
                "servers": [ { "url": "https://pets.example" } ],
                "parameters": [
                  { "$ref": "#/components/parameters/PetId" },
                  { "name": "X-Tenant", "in": "header", "description": "Shared tenant", "schema": { "type": "string" } }
                ],
                "get": { "summary": "Get a pet" },
                "delete": {
                  "summary": "Delete a pet",
                  "parameters": [
                    { "name": "x-tenant", "in": "header", "description": "Tenant that owns the pet", "schema": { "type": "string" } },
                    { "name": "id", "in": "query", "schema": { "type": "string" } }
                  ]
                }
              } }
            }
            """);

        var endpoints = svc.ParseEndpoints(doc).ToList();

        Assert.Equal(["GET", "DELETE"], endpoints.Select(e => e.Verb));
        var get = endpoints[0].Parameters!;
        Assert.Equal(["id:path", "X-Tenant:header"], get.Select(p => $"{p.Name}:{p.In}"));
        Assert.True(get[0].Required);
        Assert.Equal("integer", get[0].SchemaType);
        var delete = endpoints[1].Parameters!;
        Assert.Equal(["id:path", "x-tenant:header", "id:query"], delete.Select(p => $"{p.Name}:{p.In}"));
        Assert.Equal("Tenant that owns the pet", delete[1].Description);
    }

    [Fact]
    public void ParseEndpoints_Resolves_Component_Parameter_References()
    {
        var (svc, _) = Create();
        using var doc = JsonDocument.Parse("""
            {
              "openapi": "3.0.0",
              "components": { "parameters": {
                "PetId": { "name": "id", "in": "path", "required": true, "schema": { "type": "integer" } },
                "Limit": { "name": "limit", "in": "query", "description": "Page size", "schema": { "type": "integer" } },
                "PageSize": { "$ref": "#/components/parameters/Limit" }
              } },
              "paths": { "/pets/{id}/toys": { "get": { "parameters": [
                { "$ref": "#/components/parameters/PetId" },
                { "$ref": "#/components/parameters/PageSize" }
              ] } } }
            }
            """);

        var parameters = svc.ParseEndpoints(doc).Single().Parameters!;

        Assert.Equal(["id:path", "limit:query"], parameters.Select(p => $"{p.Name}:{p.In}"));
        Assert.True(parameters[0].Required);
        Assert.Equal("integer", parameters[0].SchemaType);
        Assert.Equal("Page size", parameters[1].Description);
    }

    [Fact]
    public void ParseEndpoints_Resolves_Swagger2_Path_Item_Parameter_References()
    {
        var (svc, _) = Create();
        using var doc = JsonDocument.Parse("""
            {
              "swagger": "2.0",
              "parameters": { "petId": { "name": "petId", "in": "path", "required": true, "type": "integer" } },
              "paths": { "/pets/{petId}": {
                "parameters": [ { "$ref": "#/parameters/petId" } ],
                "get": { "summary": "Get a pet" }
              } }
            }
            """);

        var parameter = svc.ParseEndpoints(doc).Single().Parameters!.Single();

        Assert.Equal("petId", parameter.Name);
        Assert.Equal("path", parameter.In);
        Assert.Equal("integer", parameter.SchemaType);
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

    // Issue #171 : un corps JSON dont l'objet n'a qu'une propriété ne doit pas être aplati,
    // car le corps est transmis tel quel à l'API.
    [Theory]
    [InlineData("""{ "$ref": "#/components/schemas/it" }""")]
    [InlineData("""{ "type": "object", "title": "it", "required": ["question"], "properties": { "question": { "type": "string", "title": "Question", "description": "The user's question" } } }""")]
    public void ParseEndpoints_Keeps_Single_Property_Json_Body_Object(string bodySchema)
    {
        var (svc, _) = Create();
        using var doc = JsonDocument.Parse($$"""
            {
              "openapi": "3.0.0",
              "paths": { "/ask": { "post": {
                "summary": "Ask a question",
                "requestBody": { "content": { "application/json": { "schema": {{bodySchema}} } } }
              } } },
              "components": { "schemas": { "it": {
                "type": "object",
                "title": "it",
                "required": ["question"],
                "properties": { "question": { "type": "string", "title": "Question", "description": "The user's question" } }
              } } }
            }
            """);

        var ep = svc.ParseEndpoints(doc).Single();
        var inputSchema = McpTool.GenerateInputSchema(ep.Parameters!);

        Assert.Equal("body", Assert.Single(inputSchema["required"]!.AsArray())!.GetValue<string>());
        var body = inputSchema["properties"]!["body"]!;
        Assert.Equal("object", body["type"]!.GetValue<string>());
        Assert.Equal("question", Assert.Single(body["required"]!.AsArray())!.GetValue<string>());
        var question = body["properties"]!["question"]!;
        Assert.Equal("string", question["type"]!.GetValue<string>());
        Assert.Equal("The user's question", question["description"]!.GetValue<string>());
    }

    [Fact]
    public void ParseEndpoints_Keeps_Header_Parameter_Serialization()
    {
        var (svc, _) = Create();
        using var doc = JsonDocument.Parse("""
            {
              "openapi": "3.0.0",
              "paths": { "/pets": { "get": {
                "parameters": [
                  { "name": "X-Ids", "in": "header", "schema": { "type": "array", "items": { "type": "integer" } } },
                  { "name": "X-Filter", "in": "header", "explode": true, "schema": { "type": "object" } },
                  { "name": "X-Json", "in": "header", "content": { "application/json": { "schema": { "type": "object" } } } }
                ]
              } } }
            }
            """);

        var parameters = svc.ParseEndpoints(doc).Single().Parameters!;

        var ids = parameters.Single(p => p.Name == "X-Ids");
        Assert.Equal("array", ids.SchemaType);
        Assert.False(ids.Explode);
        Assert.False(ids.IsJsonContent);
        Assert.True(parameters.Single(p => p.Name == "X-Filter").Explode);
        var json = parameters.Single(p => p.Name == "X-Json");
        Assert.True(json.IsJsonContent);
        Assert.Equal("object", json.SchemaType);
    }

    [Fact]
    public void ParseEndpoints_Keeps_Query_Parameter_Serialization()
    {
        var (svc, _) = Create();
        using var doc = JsonDocument.Parse("""
            {
              "openapi": "3.0.0",
              "paths": { "/pets": { "get": {
                "parameters": [
                  { "name": "ids", "in": "query", "schema": { "type": "array", "items": { "type": "integer" } } },
                  { "name": "tags", "in": "query", "explode": false, "schema": { "type": "array", "items": { "type": "string" } } },
                  { "name": "colors", "in": "query", "style": "pipeDelimited", "schema": { "type": "array", "items": { "type": "string" } } },
                  { "name": "filter", "in": "query", "style": "deepObject", "explode": true, "schema": { "type": "object" } }
                ]
              } } }
            }
            """);

        var parameters = svc.ParseEndpoints(doc).Single().Parameters!;

        var ids = parameters.Single(p => p.Name == "ids");
        Assert.Equal("form", ids.Style);
        Assert.True(ids.Explode);
        var tags = parameters.Single(p => p.Name == "tags");
        Assert.Equal("form", tags.Style);
        Assert.False(tags.Explode);
        var colors = parameters.Single(p => p.Name == "colors");
        Assert.Equal("pipeDelimited", colors.Style);
        Assert.False(colors.Explode);
        var filter = parameters.Single(p => p.Name == "filter");
        Assert.Equal("deepObject", filter.Style);
        Assert.True(filter.Explode);
    }

    [Theory]
    [InlineData(null, "form", false)]
    [InlineData("csv", "form", false)]
    [InlineData("multi", "form", true)]
    [InlineData("ssv", "spaceDelimited", false)]
    [InlineData("pipes", "pipeDelimited", false)]
    [InlineData("tsv", "tabDelimited", false)]
    public void ParseEndpoints_Maps_Swagger2_CollectionFormat(string? collectionFormat, string expectedStyle,
        bool expectedExplode)
    {
        var (svc, _) = Create();
        var format = collectionFormat is null ? "" : $", \"collectionFormat\": \"{collectionFormat}\"";
        using var doc = JsonDocument.Parse($$"""
            {
              "swagger": "2.0",
              "paths": { "/pets": { "get": {
                "parameters": [
                  { "name": "ids", "in": "query", "type": "array", "items": { "type": "integer" }{{format}} }
                ]
              } } }
            }
            """);

        var ids = svc.ParseEndpoints(doc).Single().Parameters!.Single();

        Assert.Equal(expectedStyle, ids.Style);
        Assert.Equal(expectedExplode, ids.Explode);
    }
}
