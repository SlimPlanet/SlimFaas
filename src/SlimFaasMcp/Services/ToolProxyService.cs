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

    Task<ProxyCallResult> ExecuteToolAsync(
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

public async Task<ProxyCallResult> ExecuteToolAsync(
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
        return new ProxyCallResult { IsBinary = false, Text = "{\"error\":\"Tool not found\"}", MimeType = "application/json" };

    baseUrl ??= ExtractBaseUrl(swagger)
                ?? throw new ArgumentException("No baseUrl provided or found in Swagger");
    baseUrl = baseUrl.TrimEnd('/');

    var inputDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
                        input.GetRawText(),
                        AppJsonContext.Default.DictionaryStringJsonElement)!;

    // Path
    var callUrl = endpoint.Url;
    foreach (var parameter in endpoint.Parameters.Where(p => p.In == "path"))
        callUrl = callUrl?.Replace($"{{{parameter.Name}}}",
                                  parameter.Name != null && inputDict.TryGetValue(parameter.Name, out var v) ? v.ToString() : "");

    if (callUrl != null && !callUrl.StartsWith('/')) callUrl = "/" + callUrl;
    var fullUrl = baseUrl + callUrl;

    // Query
    var queryParams = endpoint.Parameters
        .Where(p => p is { In: "query", Name: not null } && inputDict.ContainsKey(p.Name))
        .Select(p => $"{p.Name}={Uri.EscapeDataString(inputDict[p.Name].ValueKind == JsonValueKind.String ? inputDict[p.Name].GetString()! : inputDict[p.Name].GetRawText())}");
    if (queryParams.Any()) fullUrl += "?" + string.Join('&', queryParams);

    // Décision de type de corps
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

    var treatAsMultipart = isDeclaredMultipart || hasFormParams;
    var treatAsOctet     = !treatAsMultipart && (isDeclaredOctet || (hasBinaryBody && !hasFormParams));

    HttpResponseMessage resp;

    var httpMethod = new HttpMethod(endpoint.Verb ?? "GET");
    if (string.Equals(endpoint.Verb, "GET", StringComparison.OrdinalIgnoreCase))
    {
        var reqGet = new HttpRequestMessage(HttpMethod.Get, fullUrl);
        reqGet.Headers.Accept.ParseAdd("application/json");
        if (additionalHeaders != null)
            foreach (var header in additionalHeaders)
                reqGet.Headers.Add(header.Key, header.Value);

        resp = await _httpClient.SendAsync(reqGet);
        return await ToProxyCallResult(resp);
    }

    HttpRequestMessage reqMsg;

    static string Strip(string s)
    {
        var i = s.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? s[(i + 8)..] : s;
    }

    // Envoi
    if (treatAsMultipart)
    {
        var mp = new MultipartFormDataContent();
        foreach (var p in endpoint.Parameters.Where(x => string.Equals(x.In, "formData", StringComparison.OrdinalIgnoreCase)))
        {
            if (string.IsNullOrEmpty(p.Name)) continue;
            if (!inputDict.TryGetValue(p.Name, out var val)) continue;

            if (string.Equals(p.Format, "binary", StringComparison.OrdinalIgnoreCase))
            {
                // { data, filename?, mimeType? }
                string? b64 = null; string? filename = null; string? mime = null;
                if (val.ValueKind == JsonValueKind.Object)
                {
                    if (val.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String) b64 = d.GetString();
                    if (val.TryGetProperty("filename", out var f) && f.ValueKind == JsonValueKind.String) filename = f.GetString();
                    if (val.TryGetProperty("mimeType", out var m) && m.ValueKind == JsonValueKind.String) mime = m.GetString();
                }
                else if (val.ValueKind == JsonValueKind.String)
                {
                    b64 = val.GetString();
                }

                if (string.IsNullOrWhiteSpace(b64))
                    throw new ArgumentException($"Binary field '{p.Name}' missing base64 data");

                var bytes = Convert.FromBase64String(Strip(b64!));
                var part = new ByteArrayContent(bytes);
                part.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime);
                mp.Add(part, p.Name!, string.IsNullOrWhiteSpace(filename) ? p.Name : filename);
            }
            else
            {
                mp.Add(new StringContent(val.ValueKind == JsonValueKind.String ? val.GetString()! : val.GetRawText()), p.Name!);
            }
        }

        reqMsg = new HttpRequestMessage(httpMethod, fullUrl) { Content = mp };
    }
    else if (treatAsOctet)
    {
        // OCTET-STREAM strict
        JsonElement src;
        if (inputDict.TryGetValue("body", out var vBody))
            src = vBody;
        else if (inputDict.TryGetValue("file", out var vFile))
            src = vFile;
        else
            throw new ArgumentException("Missing binary payload (expected 'body' or 'file' with { data: <base64> })");

        string? b64 = null;
        if (src.ValueKind == JsonValueKind.Object && src.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.String)
            b64 = d.GetString();
        else if (src.ValueKind == JsonValueKind.String)
            b64 = src.GetString();

        if (string.IsNullOrWhiteSpace(b64))
            throw new ArgumentException("Binary payload must contain base64 'data'");

        var bytes = Convert.FromBase64String(Strip(b64!));
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType  = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        content.Headers.ContentLength = bytes.LongLength;

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

    reqMsg.Headers.Accept.ParseAdd("*/*");
    if (additionalHeaders != null)
        foreach (var header in additionalHeaders)
            reqMsg.Headers.Add(header.Key, header.Value);

    resp = await _httpClient.SendAsync(reqMsg);
    return await ToProxyCallResult(resp);
}

private static async Task<ProxyCallResult> ToProxyCallResult(HttpResponseMessage resp)
{
    var mediaType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
    var disp      = resp.Content.Headers.ContentDisposition;
    var fileName  = disp?.FileNameStar ?? disp?.FileName;

    // Heuristique "binaire"
    bool looksBinary =
        (mediaType is not null && (
            mediaType.StartsWith("application/octet-stream")
            || mediaType.StartsWith("image/")
            || mediaType.StartsWith("application/pdf")
            || mediaType.StartsWith("application/zip")
            || (!mediaType.StartsWith("text/") && !mediaType.EndsWith("+json") && mediaType != "application/json")
        ))
        || fileName is not null; // attachment

    if (looksBinary)
    {
        var bytes = await resp.Content.ReadAsByteArrayAsync();
        return new ProxyCallResult
        {
            IsBinary = true,
            MimeType = mediaType ?? "application/octet-stream",
            FileName = fileName,
            Bytes    = bytes
        };
    }

    // Texte / JSON
    var text = await resp.Content.ReadAsStringAsync();
    return new ProxyCallResult
    {
        IsBinary = false,
        MimeType = mediaType ?? "application/json",
        Text     = text
    };
}


}
