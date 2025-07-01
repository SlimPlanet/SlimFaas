using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using SlimFaasMcp.Models;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;

public class SwaggerService(HttpClient httpClient, IMemoryCache memoryCache)
{
    private static readonly TimeSpan SlidingExpiration = TimeSpan.FromMinutes(20);


    public async Task<JsonDocument> GetSwaggerAsync(string swaggerUrl)
    {
        // Cache key
        var cacheKey = $"swagger::{swaggerUrl}";

        // Try get from cache
        if (memoryCache.TryGetValue<JsonDocument>(cacheKey, out var cachedSwagger))
        {
            return cachedSwagger;
        }

        // Otherwise fetch and cache
        var swaggerStr = await httpClient.GetStringAsync(swaggerUrl);
        var swaggerJson = JsonDocument.Parse(swaggerStr);

        // Cache with sliding expiration
        memoryCache.Set(cacheKey, swaggerJson, new MemoryCacheEntryOptions
        {
            SlidingExpiration = SlidingExpiration
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
                    foreach (var param in parametersArray.EnumerateArray())
                    {
                        parameters.Add(new Parameter
                        {
                            Name = param.GetProperty("name").GetString(),
                            In = param.GetProperty("in").GetString(),
                            Required = param.TryGetProperty("required", out var req) ? req.GetBoolean() : false,
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
                                    ? reqArr.EnumerateArray().Select(x => x.GetString()).ToHashSet()
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


                endpoints.Add(new Endpoint
                {
                    Name = verb.ToLower() + url.Replace("/", "_").Replace("{", "").Replace("}", ""),
                    Url = url,
                    Verb = verb,
                    Summary = summary,
                    Parameters = parameters,
                    ContentType = contentType
                });
            }
        }


        return endpoints;
    }
}
