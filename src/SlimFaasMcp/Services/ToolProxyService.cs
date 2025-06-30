using System.Text;
using System.Text.Json;
using SlimFaasMcp.Models;
using YamlDotNet.Serialization;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;


public class ToolProxyService
{
    private readonly SwaggerService _swaggerService;
    private readonly HttpClient _httpClient = new HttpClient();

    public ToolProxyService(SwaggerService swaggerService)
    {
        _swaggerService = swaggerService;
    }



    private static string CombineBaseUrl(string? baseUrl, string endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
            return endpointUrl;
        if (endpointUrl.StartsWith("http"))
            return endpointUrl;
        return baseUrl.TrimEnd('/') + "/" + endpointUrl.TrimStart('/');
    }

    private static string GetContentType(Endpoint endpoint)
    {
        var contentType = "application/json"; // valeur par défaut

        // Recherche du vrai content-type
        if (endpoint.Parameters.Any(p => p.In == "formData"))
            contentType = "multipart/form-data";
        return contentType;
    }

    public async Task<List<McpTool>> GetToolsAsync(string swaggerUrl, string? baseUrl)
    {
        var swagger = await _swaggerService.GetSwaggerAsync(swaggerUrl);
        var endpoints = _swaggerService.ParseEndpoints(swagger);

        return endpoints.Select(e => new McpTool
        {
            Name = e.Name,
            Description = e.Summary,
            InputSchema = McpTool.GenerateInputSchema(e.Parameters),
            Endpoint = new McpTool.EndpointInfo
            {
                Url = CombineBaseUrl(baseUrl, e.Url),
                Method = e.Verb,
                ContentType = GetContentType(e)
            }
        }).ToList();
    }

    public static string? ExtractBaseUrl(JsonDocument swagger)
    {
        var root = swagger.RootElement;

        // OpenAPI v3: "servers": [ { "url": "https://..." }, ... ]
        if (root.TryGetProperty("servers", out var serversArr) &&
            serversArr.ValueKind == JsonValueKind.Array &&
            serversArr.GetArrayLength() > 0)
        {
            var server = serversArr[0];
            if (server.TryGetProperty("url", out var urlProp))
                return urlProp.GetString();
        }
        // OpenAPI v2: "host" + optional "basePath"
        if (root.TryGetProperty("host", out var hostProp))
        {
            var scheme = "https"; // Par défaut
            if (root.TryGetProperty("schemes", out var schemes) && schemes.ValueKind == JsonValueKind.Array && schemes.GetArrayLength() > 0)
                scheme = schemes[0].GetString() ?? scheme;
            var host = hostProp.GetString();
            var basePath = root.TryGetProperty("basePath", out var bp) ? bp.GetString() : "";
            return $"{scheme}://{host}{basePath}";
        }
        return null; // Pas trouvé
    }

public async Task<object> ExecuteToolAsync(string swaggerUrl, string toolName, object input, string? baseUrl = null)
{
    var swagger = await _swaggerService.GetSwaggerAsync(swaggerUrl);
    var endpoints = _swaggerService.ParseEndpoints(swagger);
    var endpoint = endpoints.FirstOrDefault(e => e.Name == toolName);

    if (endpoint == null)
        return new { error = "Tool not found" };

    // 1. Compose base URL
    if (string.IsNullOrEmpty(baseUrl))
        baseUrl = ExtractBaseUrl(swagger) ?? throw new Exception("No baseUrl provided or found in Swagger");
    baseUrl = baseUrl.TrimEnd('/');

    // 2. Compose full call URL
    var callUrl = endpoint.Url;
    var inputDict = JsonSerializer.Deserialize<Dictionary<string, object>>(input.ToString());
    foreach (var p in endpoint.Parameters.Where(p => p.In == "path"))
        callUrl = callUrl.Replace("{" + p.Name + "}", inputDict.TryGetValue(p.Name, out var val) ? val?.ToString() : "");

    if (!callUrl.StartsWith("/")) callUrl = "/" + callUrl;
    var fullUrl = baseUrl + callUrl;

    var queryParams = endpoint.Parameters.Where(p => p.In == "query")
        .Where(p => inputDict.ContainsKey(p.Name))
        .Select(p => $"{p.Name}={inputDict[p.Name]}");
    if (queryParams.Any())
        fullUrl += "?" + string.Join("&", queryParams);

    HttpResponseMessage resp;
    if (endpoint.Verb == "GET")
    {
        resp = await _httpClient.GetAsync(fullUrl);
    }
    else
    {
        StringContent? body = null;
        if (endpoint.Parameters.Any(p => p.In == "body"))
        {
            // ➡️ Si le schéma MCP attend {"body": { ... }}, alors inputDict["body"] est le vrai payload
            // ➡️ Sinon, on envoie tout le inputDict
            object toSend;
            if (inputDict.Count == 1 && inputDict.ContainsKey("body"))
            {
                // MCP generated a wrapper, only send the inner object
                var bodyValue = inputDict["body"];
                // Handle JsonElement or direct object
                if (bodyValue is JsonElement el)
                    toSend = el;
                else
                    toSend = bodyValue;
            }
            else
            {
                // No wrapper, send all as-is
                toSend = inputDict;
            }
            var json = JsonSerializer.Serialize(toSend);
            body = new StringContent(json, Encoding.UTF8, "application/json");
        }

        resp = await _httpClient.SendAsync(new HttpRequestMessage(
            new HttpMethod(endpoint.Verb), fullUrl)
        {
            Content = body
        });
    }

    var resultStr = await resp.Content.ReadAsStringAsync();
    try
    {
        return JsonSerializer.Deserialize<object>(resultStr);
    }
    catch
    {
        return resultStr;
    }
}


    public async Task<string> GenerateManifestYamlAsync(string swaggerUrl, string base_url)
    {
        var tools = await GetToolsAsync(swaggerUrl, base_url);
        var manifest = new SlimFaasManifest
        {
            Name = "mcp-swagger-proxy",
            Description = "Proxy MCP généré dynamiquement",
            Tools = tools
        };

        var serializer = new SerializerBuilder().Build();
        return serializer.Serialize(manifest);
    }
}
