using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Caching.Memory;
using SlimFaasMcp.Models;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;

public interface ISwaggerService
{
    Task<JsonDocument> GetSwaggerAsync(string swaggerUrl, string? baseUrl = null, string? authHeader = null);
    IEnumerable<Endpoint> ParseEndpoints(JsonDocument swagger);
}

public class SwaggerService(IHttpClientFactory httpClientFactory, IMemoryCache memoryCache) : ISwaggerService
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("InsecureHttpClient");

    private static readonly TimeSpan s_slidingExpiration = TimeSpan.FromMinutes(20);

    public async Task<JsonDocument> GetSwaggerAsync(string swaggerUrl, string? baseUrl = null, string? authHeader = null)
    {
        // Cache key
        var cacheKey = $"swagger::{swaggerUrl}";

        // Try get from cache
        if (memoryCache.TryGetValue<JsonDocument>(cacheKey, out var cachedSwagger) && cachedSwagger != null)
        {
            return cachedSwagger;
        }

        // Préparation de la requête HTTP
        using var request = new HttpRequestMessage(HttpMethod.Get, swaggerUrl);

        // Si le swaggerUrl commence par baseUrl et qu'on a un authHeader, injecter l'en-tête Authorization
        if (!string.IsNullOrEmpty(baseUrl) &&
            swaggerUrl.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(authHeader))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", authHeader);
        }

        // Envoi de la requête
        using var response = await _httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var swaggerStr = await response.Content.ReadAsStringAsync();

        // Parsing JSON
        var swaggerJson = JsonDocument.Parse(swaggerStr);

        // Mise en cache avec expiration glissante
        memoryCache.Set(cacheKey, swaggerJson, new MemoryCacheEntryOptions
        {
            SlidingExpiration = s_slidingExpiration
        });

        return swaggerJson;
    }
    public IEnumerable<Endpoint> ParseEndpoints(JsonDocument swagger)
    {
        var root = swagger.RootElement;
        var paths = root.GetProperty("paths");
        var endpoints = new List<Endpoint>();
        var expander = new OpenApiSchemaExpander(swagger.RootElement);
        foreach (var path in paths.EnumerateObject())
        {
            var url = path.Name;
            foreach (var verbObj in path.Value.EnumerateObject())
            {
                var verb = verbObj.Name.ToUpper();
                var operation = verbObj.Value;

                var summary = operation.TryGetProperty("summary", out var s) ? s.GetString() : verb + " " + url;
                var parameters = new List<Parameter>();
                string contentType = "application/json"; // default

                // Params in path/query
                if (operation.TryGetProperty("parameters", out var parametersArray))
                {
                    foreach (var param0 in parametersArray.EnumerateArray())
                    {
                        var param = param0;
                        // Résolution $ref
                        if (param.TryGetProperty("$ref", out var refProp))
                        {
                            var refPath = refProp.GetString();
                            if (refPath != null && refPath.StartsWith("#/parameters/"))
                            {
                                var paramName = refPath.Substring("#/parameters/".Length);
                                if (swagger.RootElement.TryGetProperty("parameters", out var globalParams) &&
                                    globalParams.TryGetProperty(paramName, out var resolvedParam))
                                {
                                    param = resolvedParam;
                                }
                                else
                                {
                                    // $ref non résolu
                                    continue;
                                }
                            }
                        }

                        parameters.Add(new Parameter
                        {
                            Name = param.GetProperty("name").GetString(),
                            In = param.GetProperty("in").GetString(),
                            Required = param.TryGetProperty("required", out var req) && req.GetBoolean(),
                            Description = param.TryGetProperty("description", out var d) ? d.GetString() : "",
                            SchemaType =
                                param.TryGetProperty("schema", out var sch) &&
                                sch.TryGetProperty("type", out var typ)
                                    ? typ.GetString()
                                    : "string",

                        });
                    }
                }

                // Body (OpenAPI v3)
                if (operation.TryGetProperty("requestBody", out var body))
                {
                    if (body.TryGetProperty("content", out var content))
                    {
                        if (content.TryGetProperty("application/json", out var appJson))
                        {
                            if (appJson.TryGetProperty("schema", out var schema))
                            {
                                parameters.Add(new Parameter
                                {
                                    Name = "body",
                                    In = "body",
                                    Required = true,
                                    Description = "Request body",
                                    SchemaType =
                                        schema.TryGetProperty("type", out var t) ? t.GetString() : "object",
                                    Schema = expander.ExpandSchema(schema)
                                });
                            }
                        }

                        // Multipart body
                        if (content.TryGetProperty("multipart/form-data", out var multipart))
                        {
                            if (multipart.TryGetProperty("schema", out var schema) &&
                                schema.TryGetProperty("properties", out var props))
                            {
                                var requiredFields = schema.TryGetProperty("required", out var reqArr)
                                    ? reqArr.EnumerateArray().Select(x => x.GetString()).ToHashSet()!
                                    : new HashSet<string>();
                                foreach (var prop in props.EnumerateObject())
                                {
                                    var p = prop.Value;
                                    parameters.Add(new Models.Parameter
                                    {
                                        Name = prop.Name,
                                        In = "formData",
                                        Required = requiredFields.Contains(prop.Name),
                                        Description =
                                            p.TryGetProperty("description", out var d) ? d.GetString() : "",
                                        SchemaType = p.TryGetProperty("type", out var t) ? t.GetString() : "string",
                                        Format = p.TryGetProperty("format", out var f) ? f.GetString() : null,
                                        Schema = expander.ExpandSchema(schema)
                                    });
                                }
                            }
                        }
                    }
                }

                // juste après avoir créé 'parameters' :
                JsonNode? responseSchema = null;

                // OpenAPI v3 – on cherche le 200 (ou default) en JSON
                if (operation.TryGetProperty("responses", out var responses))
                {
                    JsonElement resp;
                    if      (responses.TryGetProperty("200",     out resp) ||
                             responses.TryGetProperty("default", out resp))
                    {
                        if (resp.TryGetProperty("content", out var content) &&
                            content.TryGetProperty("application/json", out var appJson) &&
                            appJson.TryGetProperty("schema", out var schema))
                        {
                            responseSchema = SchemaHelpers.ToJsonNode(
                                new OpenApiSchemaExpander(swagger.RootElement)
                                    .ExpandSchema(schema));
                        }
                    }
                }


                endpoints.Add(new Endpoint
                {
                    Name = verb.ToLower() + url.Replace("/", "_").Replace("{", "").Replace("}", ""),
                    Url = url,
                    Verb = verb,
                    Summary = summary,
                    Parameters = parameters,
                    ContentType = contentType,
                    ResponseSchema = responseSchema

                });
            }
        }


        return endpoints;
    }
}
