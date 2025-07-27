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
      description
      fields(includeDeprecated: true) {
        name
        description
        args {
          name
          description
          type { kind name ofType { kind name ofType { kind name } } }
        }
        type  { kind name ofType { kind name ofType { kind name } } }
      }
      inputFields {                       # ← ajouté
        name
        description
        type { kind name ofType { kind name ofType { kind name } } }
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

/* helper commun — mettez‑le en début de classe */
private static JsonElement Unwrap(JsonElement node)
{
    while (node.ValueKind == JsonValueKind.Object &&
           node.TryGetProperty("kind", out var k) &&
           (k.GetString() is "NON_NULL" or "LIST") &&
           node.TryGetProperty("ofType", out var inner) &&
           inner.ValueKind == JsonValueKind.Object)
    {
        node = inner;
    }
    return node;
}

private static string MapScalar(string? gql) => gql switch
{
    "Int"     => "integer",
    "Float"   => "number",
    "Boolean" => "boolean",
    "ID"      => "string",
    "String"  => "string",
    _         => "string"
};

/* ---- nouvelle fonction récursive : construit 1 Parameter (et ses enfants) */
private static Parameter BuildParameter(string name,
                                        JsonElement typeElem,
                                        bool nonNull,
                                        string? description,
                                        IReadOnlyDictionary<string, JsonElement> typesByName)
{
    var unwrapped = Unwrap(typeElem);
    string kind   = unwrapped.GetProperty("kind").GetString()!;
    string? gqlName = unwrapped.TryGetProperty("name", out var tn) ? tn.GetString() : null;

    var param = new Parameter
    {
        Name        = name,
        Required    = nonNull,
        Description = description,
        SchemaType  = kind switch
        {
            "SCALAR" => MapScalar(gqlName),
            "ENUM"   => "string",
            "LIST"   => "array",
            "INPUT_OBJECT" => "object",
            _        => "object"
        }
    };

    // DESCENTE RÉCURSIVE
    if (kind == "INPUT_OBJECT" && gqlName != null && typesByName.TryGetValue(gqlName, out var def) &&
        def.TryGetProperty("inputFields", out var inFields) && inFields.ValueKind == JsonValueKind.Array)
    {
        foreach (var fld in inFields.EnumerateArray())
        {
            var fldName = fld.GetProperty("name").GetString()!;
            var fldType = fld.GetProperty("type");
            bool fldNonNull = fldType.GetProperty("kind").GetString() == "NON_NULL";
            string? fldDesc = fld.TryGetProperty("description", out var fd) ? fd.GetString() : null;

            param.Children.Add(
                BuildParameter(fldName, fldType, fldNonNull, fldDesc, typesByName));
        }
    }
    else if (kind == "LIST")                     // LIST<…INPUT_OBJECT…>
    {
        // on regarde le type de l’élément
        var elemType = Unwrap(typeElem.GetProperty("ofType"));
        if (elemType.GetProperty("kind").GetString() == "INPUT_OBJECT" && elemType.TryGetProperty("name", out var en))
        {
            string? elemName = en.GetString();
            if (elemName != null && typesByName.TryGetValue(elemName, out var inObj))
            {
                param.Children.AddRange(
                    BuildParameter("[]", elemType, false, null, typesByName).Children);
            }
        }
    }

    return param;
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

            /* ---------- remplace ENTIEREMENT le calcul de `parameters` ------ */
            var parameters =
                field.TryGetProperty("args", out var argsArr) && argsArr.ValueKind == JsonValueKind.Array
                    ? argsArr.EnumerateArray()
                        .Select(a =>
                        {
                            string argName = a.GetProperty("name").GetString()!;
                            var    argType = a.GetProperty("type");
                            bool   nonNull = argType.GetProperty("kind").GetString() == "NON_NULL";
                            string? argDesc = a.TryGetProperty("description", out var ad) ? ad.GetString() : null;

                            return BuildParameter(argName, argType, nonNull, argDesc, typesByName);
                        })
                        .ToList()
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
