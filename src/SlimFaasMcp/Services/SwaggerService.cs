using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using SlimFaasMcp.Models;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;

public interface ISwaggerService
{
    Task<JsonDocument> GetSwaggerAsync(string swaggerUrl, string? baseUrl = null, IDictionary<string, string>? additionalHeaders = null);
    IEnumerable<Endpoint> ParseEndpoints(JsonDocument swagger);
}

public class SwaggerService(IHttpClientFactory httpClientFactory, IMemoryCache memoryCache)
    : ISwaggerService
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("InsecureHttpClient");
    private static readonly TimeSpan s_slidingExpiration = TimeSpan.FromMinutes(20);

    public async Task<JsonDocument> GetSwaggerAsync(
        string swaggerUrl,
        string? baseUrl   = null,
        IDictionary<string, string>? additionalHeaders = null)
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

        var doc = JsonDocument.Parse(swaggerStr);
        memoryCache.Set(cacheKey, doc, new MemoryCacheEntryOptions { SlidingExpiration = s_slidingExpiration });
        return doc;
    }

    public IEnumerable<Endpoint> ParseEndpoints(JsonDocument swagger)
    {
        var root      = swagger.RootElement;
        var paths     = root.GetProperty("paths");
        var endpoints = new List<Endpoint>();
        var expander  = new OpenApiSchemaExpander(root);

        foreach (var path in paths.EnumerateObject())
        {
            var url = path.Name;
            foreach (var verbObj in path.Value.EnumerateObject())
            {
                var verb      = verbObj.Name.ToUpperInvariant();
                var operation = verbObj.Value;

                string summary = Summary(operation, verb, url);

                var parameters  = new List<Parameter>();
                string contentType = "application/json"; // défaut

                // (A) parameters (path/query/header)
                if (operation.TryGetProperty("parameters", out var parametersArray))
                {
                    foreach (var param0 in parametersArray.EnumerateArray())
                    {
                        var param = ResolveParamRefIfAny(root, param0);

                        JsonElement? enumArr = null;
                        if (param.TryGetProperty("enum", out var eArr))
                            enumArr = eArr;
                        else if (param.TryGetProperty("schema", out var sch0) && sch0.TryGetProperty("enum", out var e2))
                            enumArr = e2;

                        var descr = param.TryGetProperty("description", out var d)
                                   ? d.GetString()
                                   : "";
                        descr = AppendEnumValues(descr, enumArr);

                        parameters.Add(new Parameter
                        {
                            Name        = param.GetProperty("name").GetString(),
                            In          = param.GetProperty("in").GetString(),
                            Required    = param.TryGetProperty("required", out var req) && req.GetBoolean(),
                            Description = descr,
                            SchemaType  = param.TryGetProperty("schema", out var sch) &&
                                          sch.TryGetProperty("type", out var typ)
                                               ? typ.GetString()
                                               : "string",
                        });
                    }
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
                        parameters.Add(new Parameter
                        {
                            Name        = "body",
                            In          = "body",
                            Required    = true,
                            Description = "Request body",
                            SchemaType  = schema.TryGetProperty("type", out var t) ? t.GetString() : "object",
                            Schema      = expander.ExpandSchema(schema)
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
                        contentType = "multipart/form-data"; // ✅ priorité au multipart
                        if (multipart.TryGetProperty("schema", out var mpSchema))
                        {
                            // Si on peut lire les propriétés -> on fabrique les params
                            if (mpSchema.TryGetProperty("properties", out var props))
                            {
                                var requiredFields = mpSchema.TryGetProperty("required", out var reqArr)
                                                     ? reqArr.EnumerateArray().Select(x => x.GetString()).ToHashSet()!
                                                     : new HashSet<string>();

                                foreach (var prop in props.EnumerateObject())
                                {
                                    var p = prop.Value;

                                    JsonElement? enumArr = null;
                                    if (p.TryGetProperty("enum", out var eArr))
                                        enumArr = eArr;

                                    var descr = p.TryGetProperty("description", out var d)
                                             ? d.GetString()
                                             : "";
                                    descr = AppendEnumValues(descr, enumArr);

                                    parameters.Add(new Parameter
                                    {
                                        Name        = prop.Name,
                                        In          = "formData",
                                        Required    = requiredFields.Contains(prop.Name),
                                        Description = descr,
                                        SchemaType  = p.TryGetProperty("type", out var t) ? t.GetString() : "string",
                                        Format      = p.TryGetProperty("format", out var f) ? f.GetString() : null,
                                        Schema      = expander.ExpandSchema(mpSchema)
                                    });
                                }
                            }
                            else
                            {
                                // ✅ multipart sans properties lisibles : on synthétise un champ fichier par défaut
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
                    }
                    else if (available.Contains("application/octet-stream") && !available.Contains("application/json"))
                    {
                        // Si uniquement octet-stream (cas de ton screenshot), fige le CT à octet-stream
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
                            responseSchema = SchemaHelpers.ToJsonNode(
                                new OpenApiSchemaExpander(root).ExpandSchema(respSchema));
                        }
                    }
                }

                endpoints.Add(new Endpoint
                {
                    Name           = verb.ToLowerInvariant() + url.Replace("/", "_").Replace("{", "").Replace("}", ""),
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

    private static JsonElement ResolveParamRefIfAny(JsonElement root, JsonElement param)
    {
        if (param.TryGetProperty("$ref", out var refProp))
        {
            var refPath = refProp.GetString();
            if (refPath is not null && refPath.StartsWith("#/parameters/"))
            {
                var name = refPath.Substring("#/parameters/".Length);
                if (root.TryGetProperty("parameters", out var globals) &&
                    globals.TryGetProperty(name, out var resolved))
                    return resolved;
            }
        }
        return param;
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
}
