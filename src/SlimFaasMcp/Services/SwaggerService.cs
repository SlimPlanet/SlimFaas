using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using SlimFaasMcp.Models;
using Yaml2JsonNode;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;

public interface ISwaggerService
{
    Task<JsonDocument> GetSwaggerAsync(string swaggerUrl,
        string? baseUrl = null,
        IDictionary<string, string>? additionalHeaders = null,
        ushort? slidingExpiration = null);
    IEnumerable<Endpoint> ParseEndpoints(JsonDocument swagger);
}

public class SwaggerService(IHttpClientFactory httpClientFactory, IMemoryCache memoryCache)
    : ISwaggerService
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("InsecureHttpClient");
    private static readonly TimeSpan s_slidingExpiration = TimeSpan.FromMinutes(20);

    // The fixed fields of a path item that describe an operation. The others (parameters, summary, description,
    // servers, $ref and x- extensions) apply to the whole path.
    private static readonly HashSet<string> s_operationFields =
        new(StringComparer.OrdinalIgnoreCase) { "get", "put", "post", "delete", "options", "head", "patch", "trace" };

    // A referenced parameter can itself be a reference; the bound stops a reference cycle.
    private const int MaxParameterRefDepth = 8;

    public async Task<JsonDocument> GetSwaggerAsync(
        string swaggerUrl,
        string? baseUrl   = null,
        IDictionary<string, string>? additionalHeaders = null,
        ushort? slidingExpiration = null)
    {
        var cacheKey = $"swagger::{swaggerUrl}";
        if (memoryCache.TryGetValue<JsonDocument>(cacheKey, out var cached) && cached is not null)
            return cached;

        using var request = new HttpRequestMessage(HttpMethod.Get, swaggerUrl);

        if (!string.IsNullOrEmpty(baseUrl) &&
            swaggerUrl.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase) && additionalHeaders != null)
        {
            foreach (var h in additionalHeaders)
                request.Headers.Add(h.Key, h.Value);
        }

        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var swaggerStr = await response.Content.ReadAsStringAsync();

        JsonDocument doc;

        var trimmed = swaggerStr.TrimStart();
        if (trimmed.StartsWith('{') || trimmed.StartsWith('['))
        {
            doc = JsonDocument.Parse(swaggerStr);
        }
        else
        {
            // YamlSerializer.Deserialize<T> goes through reflection-based JSON serialization, which is not
            // AOT/trim safe. The representation-model conversion is.
            YamlStream yaml = YamlSerializer.Parse(swaggerStr);
            JsonNode node = (yaml.Documents.Count > 0 ? yaml.Documents[0].ToJsonNode() : null)
                            ?? throw new JsonException("Unable to convert Swagger YAML into JsonNode.");
            doc = JsonDocument.Parse(node.ToJsonString());
        }

        var cacheExpiration = slidingExpiration == null ? s_slidingExpiration : TimeSpan.FromMinutes(slidingExpiration.Value);

        if (cacheExpiration.TotalMinutes > 0)
        {
            memoryCache.Set(cacheKey, doc, new MemoryCacheEntryOptions { SlidingExpiration = cacheExpiration });
        }

        return doc;
    }

    public IEnumerable<Endpoint> ParseEndpoints(JsonDocument swagger)
    {
        var root      = swagger.RootElement;
        var paths     = root.GetProperty("paths");
        var endpoints = new List<Endpoint>();
        var expander  = new OpenApiSchemaExpander(root);
        var isSwagger2 = root.TryGetProperty("swagger", out _);

        foreach (var path in paths.EnumerateObject())
        {
            var url = path.Name;
            var pathParameters = ResolveParameters(root, path.Value);
            foreach (var verbObj in path.Value.EnumerateObject())
            {
                if (!s_operationFields.Contains(verbObj.Name))
                    continue;

                var verb      = verbObj.Name.ToUpperInvariant();
                var operation = verbObj.Value;

                string summary = Summary(operation, verb, url);

                var parameters  = new List<Parameter>();
                string contentType = "application/json"; // défaut

                // (A) parameters (path/query/header)
            foreach (var param in OperationParameters(root, operation, pathParameters))
            {

                // --- full schema if present (preserves anyOf/oneOf/allOf) -------------
                JsonElement? schemaEl = null;
                var isJsonContent = false;

                if (param.TryGetProperty("schema", out var sch))
                    schemaEl = sch;
                else if (param.TryGetProperty("content", out var cnt) && cnt.ValueKind == JsonValueKind.Object)
                {
                    // OpenAPI autorise content/application/json pour query params
                    if (cnt.TryGetProperty("application/json", out var cj))
                    {
                        isJsonContent = true;
                        if (cj.TryGetProperty("schema", out var sch2))
                            schemaEl = sch2;
                    }
                }

                // --- enum (to enrich the description, optional) -------------------
                JsonElement? enumArr = null;
                if (param.TryGetProperty("enum", out var eArr))
                    enumArr = eArr;
                else if (schemaEl is JsonElement sch0 && sch0.TryGetProperty("enum", out var e2))
                    enumArr = e2;

                var descr = param.TryGetProperty("description", out var d) ? d.GetString() : "";
                descr = AppendEnumValues(descr, enumArr);

                // "Simple" type (fallback; not used if Schema is provided)
                string? schemaType =
                    schemaEl is JsonElement sch1 && sch1.TryGetProperty("type", out var typEl)
                        ? typEl.GetString()
                        : (param.TryGetProperty("type", out var typLegacy) ? typLegacy.GetString() : "string");

                var location = param.GetProperty("in").GetString();
                var (style, explode) = SerializationStyle(param, location, isSwagger2);

                parameters.Add(new Parameter
                {
                    Name        = param.GetProperty("name").GetString(),
                    In          = location,
                    Required    = param.TryGetProperty("required", out var req) && TryParseBool(req, out var required) && required,
                    Description = descr,
                    SchemaType  = schemaType,
                    Style       = style,
                    Explode     = explode,
                    IsJsonContent = isJsonContent,
                    // ✅ Here: we store the expanded schema; anyOf/oneOf/allOf are preserved
                    Schema      = schemaEl is JsonElement se ? CopyDescriptionFromParameter(ExpandAndSanitize(expander, se)!,param) : null
                });
            }


                // (B) requestBody (v3)
                if (operation.TryGetProperty("requestBody", out var body) &&
                    body.TryGetProperty("content", out var content))
                {
                    // Liste des CT dispo
                    var available = content.EnumerateObject().Select(o => o.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

                    // --- JSON ---
                    if (content.TryGetProperty("application/json", out var appJson) &&
                        appJson.TryGetProperty("schema", out var schema))
                    {
                        // The JSON body is forwarded as-is, so its schema must keep every property,
                        // even when the object has only one.
                        var expanded = ExpandToElement(expander, schema);
                        parameters.Add(new Parameter
                        {
                            Name        = "body",
                            In          = "body",
                            Required    = true,
                            Description = "Request body",
                            SchemaType  = expanded.TryGetProperty("type", out var t) ? t.GetString() : "object",
                            Schema      =  ExpandAndSanitize(expander, expanded)
                        });
                        contentType = "application/json";
                    }

                    // --- OCTET-STREAM ---
                    if (content.TryGetProperty("application/octet-stream", out var _))
                    {
                        parameters.Add(new Parameter
                        {
                            Name        = "body",
                            In          = "body",
                            Required    = true,
                            Description = "Binary body",
                            SchemaType  = "string",
                            Format      = "binary",
                            Schema      = null
                        });
                        // On ne fige pas encore contentType ici; on décide après, cf. préférence multipart
                    }

                    // --- MULTIPART ---
                    var hasMultipart = content.TryGetProperty("multipart/form-data", out var multipart);
                    if (hasMultipart)
                    {
                        contentType = "multipart/form-data";
                        if (multipart.TryGetProperty("schema", out var mpSchema))
                        {
                            // 🧠 clé : appel unique et générique
                            FlattenMultipartIntoParameters(expander, mpSchema, parameters);
                        }
                        else
                        {
                            // fallback minimal
                            parameters.Add(new Parameter
                            {
                                Name        = "file",
                                In          = "formData",
                                Required    = true,
                                Description = "Binary file",
                                SchemaType  = "string",
                                Format      = "binary",
                                Schema      = null
                            });
                        }
                    }
                    else if (available.Contains("application/octet-stream") && !available.Contains("application/json"))
                    {
                        contentType = "application/octet-stream";
                    }
                }

                // (C) Output schema
                JsonNode? responseSchema = null;
                if (operation.TryGetProperty("responses", out var responses))
                {
                    JsonElement resp;
                    if (responses.TryGetProperty("200", out resp) ||
                        responses.TryGetProperty("default", out resp))
                    {
                        if (resp.TryGetProperty("content", out var respContent) &&
                            respContent.TryGetProperty("application/json", out var respJson) &&
                            respJson.TryGetProperty("schema", out var respSchema))
                        {
                            var expanded = expander.ExpandSchema(respSchema);
                            var sanitized = SchemaSanitizer.SanitizeForMcp(expanded);
                            responseSchema = SchemaHelpers.ToJsonNode(sanitized, maxDepth: 64);
                        }
                    }
                }

                endpoints.Add(new Endpoint
                {
                    Name           = verb.ToLowerInvariant() + url.Replace("/", "_", StringComparison.Ordinal).Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal),
                    Url            = url,
                    Verb           = verb,
                    Summary        = summary,
                    Parameters     = parameters,
                    ContentType    = contentType,
                    ResponseSchema = responseSchema
                });
            }
        }

        return endpoints;
    }

    // The parameters of an operation: those declared on its path item, then its own. An operation parameter replaces
    // a path item parameter with the same name and location; header names are case-insensitive.
    private static List<JsonElement> OperationParameters(JsonElement root, JsonElement operation,
        List<JsonElement> pathParameters)
    {
        var operationParameters = ResolveParameters(root, operation);

        static bool SameParameter(JsonElement a, JsonElement b)
        {
            var location = a.TryGetProperty("in", out var aIn) ? aIn.GetString() : null;
            if (!b.TryGetProperty("in", out var bIn) || bIn.GetString() != location ||
                !a.TryGetProperty("name", out var aName) || !b.TryGetProperty("name", out var bName))
                return false;

            return string.Equals(aName.GetString(), bName.GetString(),
                location == "header" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }

        return pathParameters
            .Where(shared => !operationParameters.Any(own => SameParameter(own, shared)))
            .Concat(operationParameters)
            .ToList();
    }

    private static List<JsonElement> ResolveParameters(JsonElement root, JsonElement owner) =>
        owner.TryGetProperty("parameters", out var parametersArray) && parametersArray.ValueKind == JsonValueKind.Array
            ? parametersArray.EnumerateArray().Select(param => ResolveParamRefIfAny(root, param)).ToList()
            : [];

    // Resolves a local reference such as #/components/parameters/Limit (OpenAPI 3) or #/parameters/limit
    // (Swagger 2.0). A reference that cannot be resolved is returned as is.
    private static JsonElement ResolveParamRefIfAny(JsonElement root, JsonElement param)
    {
        for (var depth = 0; depth < MaxParameterRefDepth; depth++)
        {
            if (param.ValueKind != JsonValueKind.Object ||
                !param.TryGetProperty("$ref", out var refProp) ||
                refProp.ValueKind != JsonValueKind.String ||
                refProp.GetString() is not { } refPath ||
                !refPath.StartsWith("#/", StringComparison.Ordinal))
                return param;

            var resolved = root;
            foreach (var token in refPath[2..].Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                if (resolved.ValueKind != JsonValueKind.Object ||
                    !resolved.TryGetProperty(UnescapeJsonPointer(token), out var next))
                    return param;
                resolved = next;
            }

            param = resolved;
        }

        return param;
    }

    private static string UnescapeJsonPointer(string token) =>
        token.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);

    private static bool TryParseBool(JsonElement element, out bool result)
    {
        result = false;

        switch (element.ValueKind)
        {
            case JsonValueKind.True:
                result = true;
                return true;
            case JsonValueKind.False:
                result = false;
                return true;
            case JsonValueKind.String:
                var str = element.GetString();
                if (bool.TryParse(str, out var parsed))
                {
                    result = parsed;
                    return true;
                }
                break;
        }

        return false;
    }

    // OpenAPI 3 defaults to the form style with explode for query and cookie parameters, and to the simple style
    // without explode for path and header parameters. Swagger 2.0 has no style: arrays use collectionFormat, csv
    // (comma-separated) by default and multi to repeat the parameter.
    private static (string Style, bool Explode) SerializationStyle(JsonElement param, string? location, bool isSwagger2)
    {
        var defaultStyle = location is "query" or "cookie" or "formData" ? "form" : "simple";

        if (isSwagger2)
        {
            var collectionFormat = param.TryGetProperty("collectionFormat", out var cf) && cf.ValueKind == JsonValueKind.String
                ? cf.GetString()
                : null;
            return collectionFormat switch
            {
                "multi" => ("form", true),
                "ssv" => ("spaceDelimited", false),
                "pipes" => ("pipeDelimited", false),
                "tsv" => ("tabDelimited", false),
                _ => (defaultStyle, false)
            };
        }

        var style = param.TryGetProperty("style", out var styleEl) && styleEl.ValueKind == JsonValueKind.String
            ? styleEl.GetString()!
            : defaultStyle;
        var explode = param.TryGetProperty("explode", out var explodeEl) && TryParseBool(explodeEl, out var explicitExplode)
            ? explicitExplode
            : style == "form";
        return (style, explode);
    }

    private static object CopyDescriptionFromParameter(object expanded, JsonElement param)
    {
        // if the schema does not have a description, inherit it from the parameter
        if (expanded is Dictionary<string, object> dd)
        {
            var descr = param.TryGetProperty("description", out var d) ? d.GetString() : null;
            if (!dd.TryGetValue("description", out var dv) || string.IsNullOrWhiteSpace(dv?.ToString()))
            {
                if (!string.IsNullOrWhiteSpace(descr))
                    dd["description"] = descr!;
            }
        }

        return expanded;
    }

    private static string Summary(JsonElement operation, string verb, string url)
    {
        var summaryTxt = operation.TryGetProperty("summary", out var s) ? s.GetString() : "";
        var descrTxt   = operation.TryGetProperty("description", out var d) ? d.GetString() : "";
        var combined = string.Join(" — ", new[] { summaryTxt, descrTxt }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return string.IsNullOrWhiteSpace(combined) ? $"{verb} {url}" : combined;
    }

    private static string AppendEnumValues(string? description, JsonElement? enumArray)
    {
        if (enumArray is null || enumArray.Value.ValueKind != JsonValueKind.Array)
            return description ?? "";

        var values = enumArray.Value.EnumerateArray()
                                   .Select(e => e.GetString())
                                   .Where(v => !string.IsNullOrWhiteSpace(v))
                                   .ToArray();

        if (values.Length == 0) return description ?? "";

        var prefix = string.IsNullOrWhiteSpace(description) ? "" : description.TrimEnd() + " ";
        return $"{prefix}({string.Join(", ", values)})";
    }

    private static object? ExpandAndSanitize(OpenApiSchemaExpander expander, JsonElement schemaEl)
    {
        var expanded   = expander.ExpandSchema(schemaEl);
        var sanitized  = SchemaSanitizer.SanitizeForMcp(expanded);
        return sanitized;
    }

    // Convertit un JsonNode en JsonElement SANS serializer (pas de reflection)
    private static JsonElement ToElem(JsonNode n)
    {
        using var doc = JsonDocument.Parse(n.ToJsonString());
        return doc.RootElement.Clone();
    }

    // Expand -> object graph -> JsonElement
    private static JsonElement ExpandToElement(OpenApiSchemaExpander expander, JsonElement schemaEl)
        => ToElem(SchemaHelpers.ToJsonNode(expander.ExpandSchema(schemaEl), maxDepth: 64) ?? new JsonObject());

    private static JsonElement UnwrapSinglePropObjectIfAny(OpenApiSchemaExpander expander, JsonElement schemaEl)
    {
        var rootElem = ExpandToElement(expander, schemaEl);

        // Détection wrapper { type: "object", properties: { only: {...} }, required?: ["only"] }
        if (rootElem.ValueKind == JsonValueKind.Object
            && rootElem.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
            && string.Equals(t.GetString(), "object", StringComparison.OrdinalIgnoreCase)
            && rootElem.TryGetProperty("properties", out var propsObj) && propsObj.ValueKind == JsonValueKind.Object)
        {
            var props = propsObj.EnumerateObject().ToList();
            if (props.Count == 1)
            {
                var only = props[0];

                var okRequired = true;
                if (rootElem.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
                {
                    var set = req.EnumerateArray().Select(e => e.GetString())
                        .Where(s => !string.IsNullOrWhiteSpace(s)).ToHashSet();
                    okRequired = set.Count <= 1 && (set.Count == 0 || set.Contains(only.Name));
                }

                if (okRequired)
                {
                    var childExpanded = expander.ExpandSchema(only.Value);
                    var childNode = SchemaHelpers.ToJsonNode(childExpanded, maxDepth: 64) ?? new JsonObject();
                    return ToElem(childNode); // ✅ renvoie le schéma unwrap
                }
            }
        }

        return rootElem; // pas de wrapper → retourne l’expansion
    }

    private static void FlattenMultipartIntoParameters(
    OpenApiSchemaExpander expander,
    JsonElement schemaEl,
    List<Parameter> intoParams,
    string? prefix = null,
    HashSet<string>? requiredSet = null)
{
    // 0) Dé-wrapper si objet à propriété unique
    var unwrapped = UnwrapSinglePropObjectIfAny(expander, schemaEl);

    // 1) Lire type
    string? type = null;
    if (unwrapped.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
        type = t.GetString();

    // 2) Si object: parcourir properties
    if (string.Equals(type, "object", StringComparison.OrdinalIgnoreCase)
        && unwrapped.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
    {
        // required local
        var localRequired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (unwrapped.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in req.EnumerateArray())
                if (r.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(r.GetString()))
                    localRequired.Add(r.GetString()!);
        }

        foreach (var p in props.EnumerateObject())
        {
            var name = string.IsNullOrWhiteSpace(prefix) ? p.Name : $"{prefix}.{p.Name}";
            // Recurse
            FlattenMultipartIntoParameters(expander, p.Value, intoParams, name, localRequired);
        }
        return;
    }

    // 3) Si array
    if (string.Equals(type, "array", StringComparison.OrdinalIgnoreCase)
        && unwrapped.TryGetProperty("items", out var itemsEl))
    {
        var desc = ReadDescription(unwrapped) ?? ReadDescription(itemsEl) ?? "";

        intoParams.Add(new Parameter
        {
            Name        = prefix ?? "file",
            In          = "formData",
            Required    = requiredSet?.Contains(prefix ?? "") ?? false,
            Description = desc,                                  // ✅ description propagée
            SchemaType  = "array",
            Format      = null,
            Schema      = ExpandAndSanitize(expander, unwrapped) // ✅ on garde le schéma (préserve array)
        });
        return;
    }

// [C] Cas primitive / string[format=binary] / autres:
    {
        var schemaType = type ?? "string";
        string? format = null;
        if (unwrapped.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String)
            format = fmt.GetString();

        var isBinary = string.Equals(schemaType, "string", StringComparison.OrdinalIgnoreCase)
                       && string.Equals(format, "binary", StringComparison.OrdinalIgnoreCase);

        var desc = ReadDescription(unwrapped) ?? "";

        intoParams.Add(new Parameter
        {
            Name        = prefix ?? "file",
            In          = "formData",
            Required    = requiredSet?.Contains(prefix ?? "") ?? false,
            Description = desc,                                   // ✅ description propagée
            SchemaType  = isBinary ? "string" : schemaType,
            Format      = isBinary ? "binary" : format,
            Schema      = ExpandAndSanitize(expander, unwrapped)  // ✅ on garde le schéma
        });
    }
}
    private static string? ReadDescription(JsonElement el)
        => el.ValueKind == JsonValueKind.Object
           && el.TryGetProperty("description", out var d)
           && d.ValueKind == JsonValueKind.String
            ? d.GetString()
            : null;


}
