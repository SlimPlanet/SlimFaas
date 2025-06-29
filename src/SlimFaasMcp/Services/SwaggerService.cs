using System.Net.Http;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Generic;

namespace Services
{
    public class SwaggerService
    {
        private readonly HttpClient _httpClient = new HttpClient();

        public async Task<JsonDocument> GetSwaggerAsync(string swaggerUrl)
        {
            var swaggerStr = await _httpClient.GetStringAsync(swaggerUrl);
            return JsonDocument.Parse(swaggerStr);
        }

        public IEnumerable<Models.Endpoint> ParseEndpoints(JsonDocument swagger)
        {
            var root = swagger.RootElement;
            var paths = root.GetProperty("paths");
            var endpoints = new List<Models.Endpoint>();

            foreach (var path in paths.EnumerateObject())
            {
                var url = path.Name;
                foreach (var verbObj in path.Value.EnumerateObject())
                {
                    var verb = verbObj.Name.ToUpper();
                    var operation = verbObj.Value;

                    var summary = operation.TryGetProperty("summary", out var s) ? s.GetString() : verb + " " + url;
                    var parameters = new List<Models.Parameter>();

                    // Params in path/query
                    if (operation.TryGetProperty("parameters", out var parametersArray))
                    {
                        foreach (var param in parametersArray.EnumerateArray())
                        {
                            parameters.Add(new Models.Parameter
                            {
                                Name = param.GetProperty("name").GetString(),
                                In = param.GetProperty("in").GetString(),
                                Required = param.TryGetProperty("required", out var req) ? req.GetBoolean() : false,
                                Description = param.TryGetProperty("description", out var d) ? d.GetString() : "",
                                SchemaType = param.TryGetProperty("schema", out var sch) && sch.TryGetProperty("type", out var typ) ? typ.GetString() : "string"
                            });
                        }
                    }

                    // Body (OpenAPI v3)
                    if (operation.TryGetProperty("requestBody", out var body))
                    {
                        if (body.TryGetProperty("content", out var content) && content.TryGetProperty("application/json", out var appJson))
                        {
                            if (appJson.TryGetProperty("schema", out var schema))
                            {
                                parameters.Add(new Models.Parameter
                                {
                                    Name = "body",
                                    In = "body",
                                    Required = true,
                                    Description = "Request body",
                                    SchemaType = schema.TryGetProperty("type", out var t) ? t.GetString() : "object",
                                    Schema = schema.ToString()
                                });
                            }
                        }
                    }

                    endpoints.Add(new Models.Endpoint
                    {
                        Name = verb.ToLower() + url.Replace("/", "_").Replace("{", "").Replace("}", ""),
                        Url = url,
                        Verb = verb,
                        Summary = summary,
                        Parameters = parameters
                    });
                }
            }

            return endpoints;
        }
    }
}
