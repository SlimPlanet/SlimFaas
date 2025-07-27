using System.IO.Compression;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using SlimFaasMcp.Models;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;

public class GraphQlService(IHttpClientFactory factory, IMemoryCache cache) : IRemoteSchemaService
{
    private readonly HttpClient _http = factory.CreateClient("InsecureHttpClient");

    private const string INTROSPECTION_QUERY = @"
query Introspection {
  __schema {
    queryType  { name }
    mutationType { name }
    types {
      kind
      name
      description                # ← ajouté
      fields(includeDeprecated: true) {
        name
        description              # ← ajouté
        args  {
          name
          type { kind name ofType { kind name ofType { kind name } } }
        }
        type  { kind name ofType { kind name ofType { kind name } } }
      }
    }
  }
}";

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

        // ⚠️  Si l’API renvoie errors[], on lève une exception explicite
        if (doc.RootElement.TryGetProperty("errors", out var errs) &&
            errs.ValueKind == JsonValueKind.Array && errs.GetArrayLength() > 0)
        {
            throw new InvalidOperationException(
                "GraphQL introspection failed: " + errs[0].GetProperty("message").GetString());
        }


        cache.Set(key, doc, TimeSpan.FromMinutes(20));
        return doc;
    }


   public IEnumerable<Endpoint> ParseEndpoints(JsonDocument schema)
{
    var root = schema.RootElement.GetProperty("data").GetProperty("__schema");

    // Index rapide "nom → type"
    var typesByName = root.GetProperty("types")
                          .EnumerateArray()
                          .Where(t => t.TryGetProperty("name", out var n) &&
                                      n.GetString() is { Length: > 0 })
                          .ToDictionary(t => t.GetProperty("name").GetString()!,
                                        t => t);

    foreach (var (opKind, opTypeElem) in new[]
    {
        ("query",    root.GetProperty("queryType")),
        ("mutation", root.GetProperty("mutationType"))
    })
    {
        if (opTypeElem.ValueKind != JsonValueKind.Object ||
            !opTypeElem.TryGetProperty("name", out var opNameProp))
            continue;                                   // Pas de type pour cette op

        var opTypeName = opNameProp.GetString();
        if (opTypeName is null || !typesByName.TryGetValue(opTypeName, out var opType))
            continue;                                   // Incohérence schéma

        if (!opType.TryGetProperty("fields", out var fieldsArr) ||
            fieldsArr.ValueKind != JsonValueKind.Array)
            continue;

        foreach (var field in fieldsArr.EnumerateArray())
        {
            if (!field.TryGetProperty("name", out var fNameProp)) continue;
            string fieldName = fNameProp.GetString() ?? string.Empty;
            if (fieldName.Length == 0) continue;

            string? desc = field.TryGetProperty("description", out var d) ? d.GetString() : null;

            var parameters =
                field.TryGetProperty("args", out var argsArr) && argsArr.ValueKind == JsonValueKind.Array
                    ? argsArr.EnumerateArray()
                             .Where(a => a.TryGetProperty("name", out _))
                             .Select(a => new Parameter
                             {
                                 Name        = a.GetProperty("name").GetString()!,
                                 In          = "body",
                                 Required    = true,
                                 Description = a.TryGetProperty("description", out var ad) ? ad.GetString() : string.Empty,
                                 SchemaType  = "string"
                             }).ToList()
                    : new List<Parameter>();

            yield return new Endpoint
            {
                Name        = $"{opKind}_{fieldName}",
                Url         = "",
                Verb        = "POST",
                Summary     = desc,
                Parameters  = parameters,
                ContentType = "application/json"
            };
        }
    }
}

}
