using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlimFaasMcp.Models;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;

public interface IToolProxyService
{
    Task<List<McpTool>> GetToolsAsync(string swaggerUrl, string? baseUrl, IDictionary<string,string> additionalHeaders, string? mcpPromptB64);

    Task<string> ExecuteToolAsync(
        string swaggerUrl,
        string toolName,
        JsonElement input,              // ← plus "object"
        string? baseUrl = null,
        IDictionary<string,string>? additionalHeaders= null);

}

public class ToolProxyService(ISwaggerService swaggerService, IHttpClientFactory httpClientFactory) : IToolProxyService
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("InsecureHttpClient");
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
        if (endpoint.Parameters != null && endpoint.Parameters.Any(p => p.In == "formData"))
            contentType = "multipart/form-data";
        return contentType;
    }

    public async Task<List<McpTool>> GetToolsAsync(string swaggerUrl, string? baseUrl, IDictionary<string,string>? additionalHeaders, string? mcpPromptB64)
    {
        var swagger = await swaggerService.GetSwaggerAsync(swaggerUrl, baseUrl, additionalHeaders);
        var endpoints = swaggerService.ParseEndpoints(swagger);

        var tools = endpoints.Select(e => new McpTool
        {
            Name = e.Name,
            Description = e.Summary,
            InputSchema = McpTool.GenerateInputSchema(e.Parameters),
            OutputSchema = e.ResponseSchema ?? new JsonObject(),
            Endpoint = new McpTool.EndpointInfo
            {
                Url = CombineBaseUrl(baseUrl, e.Url),
                Method = e.Verb,
                ContentType = GetContentType(e)
            }
        }).ToList();

        var mcpPrompt = McpPrompt.ParseMcpPrompt(mcpPromptB64);
        if (mcpPrompt != null)
        {
            // Désactive les tools non listés dans activeTools si la liste existe
            if (mcpPrompt.ActiveTools != null && mcpPrompt.ActiveTools.Count > 0)
                tools = tools.Where(t => mcpPrompt.ActiveTools.Contains(t.Name)).ToList();

            // Surcharge tools
            if (mcpPrompt.Tools == null)
            {
                return tools;
            }

            {
                foreach (var ov in mcpPrompt.Tools)
                {
                    var tool = tools.FirstOrDefault(t => t.Name == ov.Name);
                    JsonNode inputSchema = null;
                    if (ov.InputSchema is not null)
                    {
                        // sérialise puis parse -> JsonNode
                        inputSchema = ov.InputSchema!;
                    }
                    if (tool != null)
                    {
                        if (ov.Description != null)
                            tool.Description = ov.Description;
                        // Quand on lit un override YAML (déjà désérialisé en object),
                        // il faut le convertir en JsonNode avant de le ranger.
                        if (inputSchema is not null)
                        {
                            tool.InputSchema = ov.InputSchema!;
                        }

                        if (ov.OutputSchema is not null)
                        {
                            tool.OutputSchema = ov.OutputSchema;
                        }
                    }
                    else
                    {
                        // Ajoute tool custom (optionnel)
                        tools.Add(new McpTool
                        {
                            Name = ov.Name,
                            Description = ov.Description ?? "",
                            InputSchema = inputSchema ?? new JsonObject() ,
                            OutputSchema = ov.OutputSchema ?? new JsonObject(),
                            Endpoint = new McpTool.EndpointInfo { Url = "", Method = "", ContentType = "application/json" }
                        });
                    }
                }
            }
        }

        return tools;
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

public async Task<string> ExecuteToolAsync(
        string swaggerUrl,
        string toolName,
        JsonElement input,              // ← plus "object"
        string? baseUrl = null,
        IDictionary<string,string>? additionalHeaders = null)
{
    var swagger   = await swaggerService.GetSwaggerAsync(swaggerUrl, baseUrl, additionalHeaders);
    var endpoints = swaggerService.ParseEndpoints(swagger);
    var endpoint  = endpoints.FirstOrDefault(e => e.Name == toolName);

    if (endpoint is null)
        return "{\"error\":\"Tool not found\"}";

    baseUrl ??= ExtractBaseUrl(swagger)
                ?? throw new ArgumentException("No baseUrl provided or found in Swagger");
    baseUrl = baseUrl.TrimEnd('/');

    // input → dictionnaire JSON
    var inputDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        input.GetRawText(),
                        AppJsonContext.Default.DictionaryStringJsonElement)!;

    // Path params
    var callUrl = endpoint.Url;
    foreach (var parameter in endpoint.Parameters.Where(p => p.In == "path"))
        callUrl = callUrl?.Replace($"{{{parameter.Name}}}",
                                  parameter.Name != null && inputDict.TryGetValue(parameter.Name, out var v) ? v.ToString() : "");

    if (callUrl != null && !callUrl.StartsWith('/')) callUrl = "/" + callUrl;
    var fullUrl = baseUrl + callUrl;

    // Query params
    var queryParams = endpoint.Parameters.Where(parameter => parameter is { In: "query", Name: not null } && inputDict.ContainsKey(parameter.Name))
                               .Select(parameter => $"{parameter.Name}={inputDict[parameter.Name]}");
    if (queryParams.Any()) fullUrl += "?" + string.Join('&', queryParams);

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
            var payload = (inputDict.Count == 1 && inputDict.ContainsKey("body"))
                            ? inputDict["body"].GetRawText()
                            : JsonSerializer.Serialize(inputDict, AppJsonContext.Default.DictionaryStringJsonElement);

            body = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        var reqMsg = new HttpRequestMessage(new HttpMethod(endpoint.Verb ?? "GET"), fullUrl)
                     { Content = body };

        if (additionalHeaders != null)
        {
            foreach (var header in additionalHeaders)
            {
                reqMsg.Headers.Add(header.Key, header.Value);
            }
        }


        resp = await _httpClient.SendAsync(reqMsg);
    }

    return await resp.Content.ReadAsStringAsync(); // on renvoie **toujours** un string JSON
}


}
