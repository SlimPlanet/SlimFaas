using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using SlimFaasMcp.Models;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;

public class GraphQlService(IHttpClientFactory factory, IMemoryCache cache) : IRemoteSchemaService
{
    private readonly HttpClient _http = factory.CreateClient("InsecureHttpClient");

    private const string INTROSPECTION_QUERY = @"query Introspection { __schema { queryType { name } mutationType { name } types { kind name description fields(includeDeprecated:true){ name description args { name description } } } }}";

    public async Task<JsonDocument> GetSchemaAsync(string url, string? baseUrl = null, string? auth = null)
    {
        var key = $"graphql::{url}";
        if (cache.TryGetValue<JsonDocument>(key, out var doc)) return doc;

        // Construction JSON sans réflexion
        var payload = new System.Text.Json.Nodes.JsonObject
        {
            ["query"] = INTROSPECTION_QUERY,
            ["variables"] = new JsonObject()
        };
        var payloadStr = payload.ToJsonString(AppJsonContext.Default.Options);

        using var resp = await _http.PostAsync(url, new StringContent(payloadStr, System.Text.Encoding.UTF8, "application/json"));
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadAsStringAsync();
        doc = JsonDocument.Parse(json);
        cache.Set(key, doc, TimeSpan.FromMinutes(20));
        return doc;
    }


    public IEnumerable<Endpoint> ParseEndpoints(JsonDocument schema)
    {
        var root = schema.RootElement.GetProperty("data").GetProperty("__schema");
        var types = root.GetProperty("types").EnumerateArray().ToList();
        foreach (var op in new[] { ("query", root.GetProperty("queryType")), ("mutation", root.GetProperty("mutationType")) })
        {
            if (op.Item2.ValueKind == JsonValueKind.Null) continue;
            var typeName = op.Item2.GetProperty("name").GetString();
            var typeDef = types.First(t => t.GetProperty("name").GetString() == typeName);
            foreach (var field in typeDef.GetProperty("fields").EnumerateArray())
            {
                var fieldName = field.GetProperty("name").GetString();
                var desc = field.TryGetProperty("description", out var d) ? d.GetString() : string.Empty;
                var parameters = field.GetProperty("args").EnumerateArray().Select(a => new Parameter
                {
                    Name = a.GetProperty("name").GetString()!,
                    In = "body",
                    Required = true,
                    Description = a.TryGetProperty("description", out var ad) ? ad.GetString() : string.Empty,
                    SchemaType = "string"
                }).ToList();

                yield return new Endpoint
                {
                    Name = $"{op.Item1}_{fieldName}",
                    Url = "",
                    Verb = "POST",
                    Summary = desc,
                    Parameters = parameters,
                    ContentType = "application/json"
                };
            }
        }
    }
}
