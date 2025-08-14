﻿using System.Net.Http.Headers;
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
        JsonElement input,
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

    // Fallback si Endpoint.ContentType est vide
    private static string GetContentType(Endpoint endpoint)
    {
        if (endpoint.Parameters != null && endpoint.Parameters.Any(p => string.Equals(p.In, "formData", StringComparison.OrdinalIgnoreCase)))
            return "multipart/form-data";
        if (endpoint.Parameters != null && endpoint.Parameters.Any(p =>
                string.Equals(p.In, "body", StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Format, "binary", StringComparison.OrdinalIgnoreCase)))
            return "application/octet-stream";
        return "application/json";
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
                Url         = CombineBaseUrl(baseUrl, e.Url),
                Method      = e.Verb,
                ContentType = string.IsNullOrWhiteSpace(e.ContentType) ? GetContentType(e) : e.ContentType
            }
        }).ToList();

        var mcpPrompt = SlimFaasMcp.Models.McpPrompt.ParseMcpPrompt(mcpPromptB64);
        if (mcpPrompt != null)
        {
            if (mcpPrompt.ActiveTools != null && mcpPrompt.ActiveTools.Count > 0)
                tools = tools.Where(t => mcpPrompt.ActiveTools.Contains(t.Name)).ToList();

            if (mcpPrompt.Tools != null)
            {
                foreach (var ov in mcpPrompt.Tools)
                {
                    var tool = tools.FirstOrDefault(t => t.Name == ov.Name);
                    JsonNode inputSchema = ov.InputSchema;

                    if (tool != null)
                    {
                        if (ov.Description != null)
                            tool.Description = ov.Description;
                        if (inputSchema is not null)
                            tool.InputSchema = inputSchema;
                        if (ov.OutputSchema is not null)
                            tool.OutputSchema = ov.OutputSchema;
                    }
                    else
                    {
                        tools.Add(new McpTool
                        {
                            Name        = ov.Name,
                            Description = ov.Description ?? "",
                            InputSchema = inputSchema ?? new JsonObject(),
                            OutputSchema= ov.OutputSchema ?? new JsonObject(),
                            Endpoint    = new McpTool.EndpointInfo { Url = "", Method = "", ContentType = "application/json" }
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

        if (root.TryGetProperty("servers", out var serversArr) &&
            serversArr.ValueKind == JsonValueKind.Array &&
            serversArr.GetArrayLength() > 0)
        {
            var server = serversArr[0];
            if (server.TryGetProperty("url", out var urlProp))
                return urlProp.GetString();
        }
        if (root.TryGetProperty("host", out var hostProp))
        {
            var scheme = "https";
            if (root.TryGetProperty("schemes", out var schemes) && schemes.ValueKind == JsonValueKind.Array && schemes.GetArrayLength() > 0)
                scheme = schemes[0].GetString() ?? scheme;
            var host = hostProp.GetString();
            var basePath = root.TryGetProperty("basePath", out var bp) ? bp.GetString() : "";
            return $"{scheme}://{host}{basePath}";
        }
        return null;
    }

    private static string? TryGetString(JsonElement e)
        => e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText();

    private static string StripDataUrlPrefix(string s)
    {
        var idx = s.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? s[(idx + 8)..] : s;
    }

    private static (byte[] data, string? fileName, string? mimeType) ExtractBinary(JsonElement val, string fallbackName)
    {
        if (val.ValueKind == JsonValueKind.Object)
        {
            string? data = null, fileName = null, mimeType = null;
            if (val.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
                data = d.GetString();
            if (val.TryGetProperty("filename", out var f) && f.ValueKind == JsonValueKind.String)
                fileName = f.GetString();
            if (val.TryGetProperty("mimeType", out var m) && m.ValueKind == JsonValueKind.String)
                mimeType = m.GetString();

            if (string.IsNullOrWhiteSpace(data))
                throw new ArgumentException($"Binary field '{fallbackName}' missing 'data' base64");

            var bytes = Convert.FromBase64String(StripDataUrlPrefix(data));
            return (bytes, fileName ?? fallbackName, string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType);
        }

        if (val.ValueKind == JsonValueKind.String)
        {
            var s = val.GetString()!;
            var bytes = Convert.FromBase64String(StripDataUrlPrefix(s));
            return (bytes, fallbackName, "application/octet-stream");
        }

        throw new ArgumentException($"Binary field '{fallbackName}' has invalid JSON type");
    }

public async Task<string> ExecuteToolAsync(
    string swaggerUrl,
    string toolName,
    JsonElement input,
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

    var inputDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        input.GetRawText(),
                        AppJsonContext.Default.DictionaryStringJsonElement)!;

    // ----- Path params
    var callUrl = endpoint.Url;
    foreach (var parameter in endpoint.Parameters.Where(p => p.In == "path"))
        callUrl = callUrl?.Replace($"{{{parameter.Name}}}",
                                  parameter.Name != null && inputDict.TryGetValue(parameter.Name, out var v) ? v.ToString() : "");

    if (callUrl != null && !callUrl.StartsWith('/')) callUrl = "/" + callUrl;
    var fullUrl = baseUrl + callUrl;

    // ----- Query params (uniquement ceux déclarés comme query)
    var queryParams = endpoint.Parameters
        .Where(p => p is { In: "query", Name: not null } && inputDict.ContainsKey(p.Name))
        .Select(p => $"{p.Name}={Uri.EscapeDataString(inputDict[p.Name].ValueKind == JsonValueKind.String ? inputDict[p.Name].GetString()! : inputDict[p.Name].GetRawText())}");
    if (queryParams.Any()) fullUrl += "?" + string.Join('&', queryParams);

    // ----- Choix du corps
    var declaredContentType = endpoint.ContentType;
    var hasFormParams = endpoint.Parameters.Any(p =>
        string.Equals(p.In, "formData", StringComparison.OrdinalIgnoreCase));
    var hasBinaryBody = endpoint.Parameters.Any(p =>
        string.Equals(p.In, "body", StringComparison.OrdinalIgnoreCase) &&
        string.Equals(p.Format, "binary", StringComparison.OrdinalIgnoreCase));

    var isDeclaredMultipart = !string.IsNullOrWhiteSpace(declaredContentType) &&
                              declaredContentType.StartsWith("multipart/form-data", StringComparison.OrdinalIgnoreCase);
    var isDeclaredOctet     = !string.IsNullOrWhiteSpace(declaredContentType) &&
                              declaredContentType.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase);

    // ✅ Règle stricte :
    // - Si le spec déclare multipart OU il y a des formData -> multipart
    // - SINON si le spec déclare octet-stream (ou body binaire sans formData) -> octet-stream
    // - Sinon -> JSON
    var treatAsMultipart = isDeclaredMultipart || hasFormParams;
    var treatAsOctet     = !treatAsMultipart && (isDeclaredOctet || (hasBinaryBody && !hasFormParams));

    // ----- Construire la requête HTTP
    HttpResponseMessage resp;

    var httpMethod = new HttpMethod(endpoint.Verb ?? "GET");
    if (string.Equals(endpoint.Verb, "GET", StringComparison.OrdinalIgnoreCase))
    {
        var reqGet = new HttpRequestMessage(HttpMethod.Get, fullUrl);
        // (optionnel) Accept JSON
        reqGet.Headers.Accept.ParseAdd("application/json");
        if (additionalHeaders != null)
            foreach (var header in additionalHeaders)
                reqGet.Headers.Add(header.Key, header.Value);

        resp = await _httpClient.SendAsync(reqGet);
        return await resp.Content.ReadAsStringAsync();
    }

    HttpRequestMessage reqMsg;

    if (treatAsMultipart)
    {
        var mp = new MultipartFormDataContent();

        if (hasFormParams)
        {
            // Utilise la déclaration des formData
            foreach (var p in endpoint.Parameters.Where(x => string.Equals(x.In, "formData", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrEmpty(p.Name)) continue;
                if (!inputDict.TryGetValue(p.Name, out var val)) continue;

                if (string.Equals(p.Format, "binary", StringComparison.OrdinalIgnoreCase))
                {
                    var (bytes, fileName, mimeType) = ExtractBinary(val, p.Name!);
                    var byteContent = new ByteArrayContent(bytes);
                    byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType ?? "application/octet-stream");
                    mp.Add(byteContent, p.Name!, string.IsNullOrWhiteSpace(fileName) ? p.Name : fileName);
                }
                else
                {
                    mp.Add(new StringContent(val.ValueKind == JsonValueKind.String ? val.GetString()! : val.GetRawText()), p.Name!);
                }
            }
        }
        else
        {
            // Pas de formData déclarés -> (rare) on tomberait ici uniquement si isDeclaredMultipart==true
            // On ne devine rien : on mappe seulement un éventuel champ "file"
            if (inputDict.TryGetValue("file", out var vfile))
            {
                var (bytes, fileName, mimeType) = ExtractBinary(vfile, "file");
                var byteContent = new ByteArrayContent(bytes);
                byteContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(mimeType ?? "application/octet-stream");
                mp.Add(byteContent, "file", string.IsNullOrWhiteSpace(fileName) ? "file" : fileName);
            }
        }

        reqMsg = new HttpRequestMessage(httpMethod, fullUrl) { Content = mp };
    }
    else if (treatAsOctet)
    {
        // ✅ OCTET-STREAM: on doit envoyer les octets bruts exactement comme l'exemple Swagger
        //   Requis: le JSON MCP contient "body": { data: <base64>, filename?, mimeType? }
        if (!inputDict.TryGetValue("body", out var valBody))
            return "{\"error\":\"Missing 'body' for binary request\"}";

        var (bytes, _, mimeType) = ExtractBinary(valBody, "body");
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
             "application/octet-stream");

        reqMsg = new HttpRequestMessage(httpMethod, fullUrl) { Content = content };
    }
    else
    {
        // JSON
        StringContent? body = null;

        if (endpoint.Parameters.Any(p => p.In == "body"))
        {
            var payload = (inputDict.Count == 1 && inputDict.ContainsKey("body"))
                            ? inputDict["body"].GetRawText()
                            : JsonSerializer.Serialize(inputDict, AppJsonContext.Default.DictionaryStringJsonElement);

            body = new StringContent(payload, Encoding.UTF8, "application/json");
        }

        reqMsg = new HttpRequestMessage(httpMethod, fullUrl) { Content = body };
    }

    // Headers communs
    reqMsg.Headers.Accept.ParseAdd("application/json");
    if (additionalHeaders != null)
        foreach (var header in additionalHeaders)
            reqMsg.Headers.Add(header.Key, header.Value);

    resp = await _httpClient.SendAsync(reqMsg);
    return await resp.Content.ReadAsStringAsync();
}

}
