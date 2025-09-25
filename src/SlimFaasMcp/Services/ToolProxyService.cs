using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlimFaasMcp.Models;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;

public interface IToolProxyService
{
    Task<List<McpTool>> GetToolsAsync(string swaggerUrl, string? baseUrl, IDictionary<string, string> additionalHeaders,
        string? mcpPromptB64);

    Task<ProxyCallResult> ExecuteToolAsync(
        string swaggerUrl,
        string toolName,
        JsonElement input,
        string? baseUrl = null,
        IDictionary<string, string>? additionalHeaders = null);
}

public class ToolProxyService(ISwaggerService swaggerService, IHttpClientFactory httpClientFactory) : IToolProxyService
{
    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("InsecureHttpClient");

    public async Task<List<McpTool>> GetToolsAsync(string swaggerUrl, string? baseUrl,
        IDictionary<string, string>? additionalHeaders, string? mcpPromptB64)
    {
        JsonDocument swagger = await swaggerService.GetSwaggerAsync(swaggerUrl, baseUrl, additionalHeaders);
        IEnumerable<Endpoint> endpoints = swaggerService.ParseEndpoints(swagger);

        List<McpTool> tools = endpoints.Select(e => new McpTool
        {
            Name = e.Name,
            Description = e.Summary,
            InputSchema = McpTool.GenerateInputSchema(e.Parameters),
            OutputSchema = e.ResponseSchema ?? new JsonObject(),
            Endpoint = new McpTool.EndpointInfo
            {
                Url = CombineBaseUrl(baseUrl, e.Url),
                Method = e.Verb,
                ContentType = string.IsNullOrWhiteSpace(e.ContentType) ? GetContentType(e) : e.ContentType
            }
        }).ToList();

        McpPrompt? mcpPrompt = McpPrompt.ParseMcpPrompt(mcpPromptB64);
        if (mcpPrompt != null)
        {
            if (mcpPrompt.ActiveTools != null && mcpPrompt.ActiveTools.Count > 0)
            {
                tools = tools.Where(t => mcpPrompt.ActiveTools.Contains(t.Name)).ToList();
            }

            if (mcpPrompt.Tools != null)
            {
                foreach (McpPrompt.McpToolOverride ov in mcpPrompt.Tools)
                {
                    McpTool? tool = tools.FirstOrDefault(t => t.Name == ov.Name);
                    JsonNode inputSchema = ov.InputSchema;

                    if (tool != null)
                    {
                        if (ov.Description != null)
                        {
                            tool.Description = ov.Description;
                        }

                        if (inputSchema is not null)
                        {
                            tool.InputSchema = inputSchema;
                        }

                        if (ov.OutputSchema is not null)
                        {
                            tool.OutputSchema = ov.OutputSchema;
                        }
                    }
                    else
                    {
                        tools.Add(new McpTool
                        {
                            Name = ov.Name,
                            Description = ov.Description ?? "",
                            InputSchema = inputSchema ?? new JsonObject(),
                            OutputSchema = ov.OutputSchema ?? new JsonObject(),
                            Endpoint = new McpTool.EndpointInfo
                            {
                                Url = "", Method = "", ContentType = "application/json"
                            }
                        });
                    }
                }
            }
        }

        return tools;
    }


    public async Task<ProxyCallResult> ExecuteToolAsync(
        string swaggerUrl,
        string toolName,
        JsonElement input,
        string? baseUrl = null,
        IDictionary<string, string>? additionalHeaders = null)
    {
        JsonDocument swagger = await swaggerService.GetSwaggerAsync(swaggerUrl, baseUrl, additionalHeaders);
        IEnumerable<Endpoint> endpoints = swaggerService.ParseEndpoints(swagger);
        Endpoint? endpoint = endpoints.FirstOrDefault(e => e.Name == toolName);

        if (endpoint is null)
        {
            return new ProxyCallResult
            {
                IsBinary = false, Text = "{\"error\":\"Tool not found\"}", MimeType = "application/json", StatusCode = 500
            };
        }

        baseUrl ??= ExtractBaseUrl(swagger)
                    ?? throw new ArgumentException("No baseUrl provided or found in Swagger");
        baseUrl = baseUrl.TrimEnd('/');

        Dictionary<string, JsonElement>? inputDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(
            input.GetRawText(),
            AppJsonContext.Default.DictionaryStringJsonElement)!;

        // Path
        string? callUrl = endpoint.Url;
        foreach (Parameter parameter in endpoint.Parameters.Where(p => p.In == "path"))
        {
            callUrl = callUrl?.Replace($"{{{parameter.Name}}}",
                parameter.Name != null && inputDict.TryGetValue(parameter.Name, out JsonElement v) ? v.ToString() : "");
        }

        if (callUrl != null && !callUrl.StartsWith('/'))
        {
            callUrl = "/" + callUrl;
        }

        string fullUrl = baseUrl + callUrl;

        // Query
        IEnumerable<string> queryParams = endpoint.Parameters
            .Where(p => p is { In: "query", Name: not null } && inputDict.ContainsKey(p.Name))
            .Select(p =>
                $"{p.Name}={Uri.EscapeDataString(inputDict[p.Name].ValueKind == JsonValueKind.String ? inputDict[p.Name].GetString()! : inputDict[p.Name].GetRawText())}");
        if (queryParams.Any())
        {
            fullUrl += "?" + string.Join('&', queryParams);
        }

        // Décision de type de corps
        string? declaredContentType = endpoint.ContentType;
        bool hasFormParams = endpoint.Parameters.Any(p =>
            string.Equals(p.In, "formData", StringComparison.OrdinalIgnoreCase));
        bool hasBinaryBody = endpoint.Parameters.Any(p =>
            string.Equals(p.In, "body", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Format, "binary", StringComparison.OrdinalIgnoreCase));

        bool isDeclaredMultipart = !string.IsNullOrWhiteSpace(declaredContentType) &&
                                   declaredContentType.StartsWith("multipart/form-data",
                                       StringComparison.OrdinalIgnoreCase);
        bool isDeclaredOctet = !string.IsNullOrWhiteSpace(declaredContentType) &&
                               declaredContentType.StartsWith("application/octet-stream",
                                   StringComparison.OrdinalIgnoreCase);

        bool treatAsMultipart = isDeclaredMultipart || hasFormParams;
        bool treatAsOctet = !treatAsMultipart && (isDeclaredOctet || (hasBinaryBody && !hasFormParams));

        HttpResponseMessage resp;

        HttpMethod httpMethod = new(endpoint.Verb ?? "GET");
        if (string.Equals(endpoint.Verb, "GET", StringComparison.OrdinalIgnoreCase))
        {
            HttpRequestMessage reqGet = new(HttpMethod.Get, fullUrl);
            reqGet.Headers.Accept.ParseAdd("application/json");
            if (additionalHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in additionalHeaders)
                {
                    reqGet.Headers.Add(header.Key, header.Value);
                }
            }

            resp = await _httpClient.SendAsync(reqGet);
            return await ToProxyCallResult(resp);
        }

        HttpRequestMessage reqMsg;

        static string Strip(string s)
        {
            int i = s.IndexOf(";base64,", StringComparison.OrdinalIgnoreCase);
            return i >= 0 ? s[(i + 8)..] : s;
        }

        // Envoi
        if (treatAsMultipart)
        {
            MultipartFormDataContent mp = new();
            foreach (Parameter p in endpoint.Parameters.Where(x =>
                         string.Equals(x.In, "formData", StringComparison.OrdinalIgnoreCase)))
            {
                if (string.IsNullOrEmpty(p.Name))
                {
                    continue;
                }

                if (!inputDict.TryGetValue(p.Name, out JsonElement val))
                {
                    continue;
                }

                if (string.Equals(p.Format, "binary", StringComparison.OrdinalIgnoreCase))
                {
                    // { data, filename?, mimeType? }
                    string? b64 = null;
                    string? filename = null;
                    string? mime = null;
                    if (val.ValueKind == JsonValueKind.Object)
                    {
                        if (val.TryGetProperty("data", out JsonElement d) && d.ValueKind == JsonValueKind.String)
                        {
                            b64 = d.GetString();
                        }

                        if (val.TryGetProperty("filename", out JsonElement f) && f.ValueKind == JsonValueKind.String)
                        {
                            filename = f.GetString();
                        }

                        if (val.TryGetProperty("mimeType", out JsonElement m) && m.ValueKind == JsonValueKind.String)
                        {
                            mime = m.GetString();
                        }
                    }
                    else if (val.ValueKind == JsonValueKind.String)
                    {
                        b64 = val.GetString();
                    }

                    if (string.IsNullOrWhiteSpace(b64))
                    {
                        throw new ArgumentException($"Binary field '{p.Name}' missing base64 data");
                    }

                    byte[] bytes = Convert.FromBase64String(Strip(b64!));
                    ByteArrayContent part = new(bytes);
                    part.Headers.ContentType =
                        new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(mime) ? "application/octet-stream" : mime);
                    mp.Add(part, p.Name!, string.IsNullOrWhiteSpace(filename) ? p.Name : filename);
                }
                else
                {
                    mp.Add(
                        new StringContent(val.ValueKind == JsonValueKind.String ? val.GetString()! : val.GetRawText()),
                        p.Name!);
                }
            }

            reqMsg = new HttpRequestMessage(httpMethod, fullUrl) { Content = mp };
        }
        else if (treatAsOctet)
        {
            // OCTET-STREAM strict
            JsonElement src;
            if (inputDict.TryGetValue("body", out JsonElement vBody))
            {
                src = vBody;
            }
            else if (inputDict.TryGetValue("file", out JsonElement vFile))
            {
                src = vFile;
            }
            else
            {
                throw new ArgumentException(
                    "Missing binary payload (expected 'body' or 'file' with { data: <base64> })");
            }

            string? b64 = null;
            if (src.ValueKind == JsonValueKind.Object && src.TryGetProperty("data", out JsonElement d) &&
                d.ValueKind == JsonValueKind.String)
            {
                b64 = d.GetString();
            }
            else if (src.ValueKind == JsonValueKind.String)
            {
                b64 = src.GetString();
            }

            if (string.IsNullOrWhiteSpace(b64))
            {
                throw new ArgumentException("Binary payload must contain base64 'data'");
            }

            byte[] bytes = Convert.FromBase64String(Strip(b64!));
            ByteArrayContent content = new(bytes);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            content.Headers.ContentLength = bytes.LongLength;

            reqMsg = new HttpRequestMessage(httpMethod, fullUrl) { Content = content };
        }
        else
        {
            // JSON
            StringContent? body = null;
            if (endpoint.Parameters.Any(p => p.In == "body"))
            {
                string payload = inputDict.Count == 1 && inputDict.ContainsKey("body")
                    ? inputDict["body"].GetRawText()
                    : JsonSerializer.Serialize(inputDict, AppJsonContext.Default.DictionaryStringJsonElement);

                body = new StringContent(payload, Encoding.UTF8, "application/json");
            }

            reqMsg = new HttpRequestMessage(httpMethod, fullUrl) { Content = body };
        }

        reqMsg.Headers.Accept.ParseAdd("*/*");
        if (additionalHeaders != null)
        {
            foreach (KeyValuePair<string, string> header in additionalHeaders)
            {
                reqMsg.Headers.Add(header.Key, header.Value);
            }
        }

        resp = await _httpClient.SendAsync(reqMsg);
        return await ToProxyCallResult(resp);
    }

    private static string CombineBaseUrl(string? baseUrl, string endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return endpointUrl;
        }

        if (endpointUrl.StartsWith("http"))
        {
            return endpointUrl;
        }

        return baseUrl.TrimEnd('/') + "/" + endpointUrl.TrimStart('/');
    }

    // Fallback si Endpoint.ContentType est vide
    private static string GetContentType(Endpoint endpoint)
    {
        if (endpoint.Parameters != null &&
            endpoint.Parameters.Any(p => string.Equals(p.In, "formData", StringComparison.OrdinalIgnoreCase)))
        {
            return "multipart/form-data";
        }

        if (endpoint.Parameters != null && endpoint.Parameters.Any(p =>
                string.Equals(p.In, "body", StringComparison.OrdinalIgnoreCase)
                && string.Equals(p.Format, "binary", StringComparison.OrdinalIgnoreCase)))
        {
            return "application/octet-stream";
        }

        return "application/json";
    }

    public static string? ExtractBaseUrl(JsonDocument swagger)
    {
        JsonElement root = swagger.RootElement;

        if (root.TryGetProperty("servers", out JsonElement serversArr) &&
            serversArr.ValueKind == JsonValueKind.Array &&
            serversArr.GetArrayLength() > 0)
        {
            JsonElement server = serversArr[0];
            if (server.TryGetProperty("url", out JsonElement urlProp))
            {
                return urlProp.GetString();
            }
        }

        if (root.TryGetProperty("host", out JsonElement hostProp))
        {
            string scheme = "https";
            if (root.TryGetProperty("schemes", out JsonElement schemes) && schemes.ValueKind == JsonValueKind.Array &&
                schemes.GetArrayLength() > 0)
            {
                scheme = schemes[0].GetString() ?? scheme;
            }

            string? host = hostProp.GetString();
            string? basePath = root.TryGetProperty("basePath", out JsonElement bp) ? bp.GetString() : "";
            return $"{scheme}://{host}{basePath}";
        }

        return null;
    }

    private static async Task<ProxyCallResult> ToProxyCallResult(HttpResponseMessage resp)
    {
        string? mediaType = resp.Content.Headers.ContentType?.MediaType?.ToLowerInvariant();
        ContentDispositionHeaderValue? disp = resp.Content.Headers.ContentDisposition;
        string? fileName = disp?.FileNameStar ?? disp?.FileName;

        bool isJson = mediaType is not null &&
                      (mediaType == "application/json" || mediaType.EndsWith("+json"));
        bool isText = mediaType is not null && mediaType.StartsWith("text/");

        // Heuristique "binaire" explicite + fallback générique
        bool looksBinary =
            (mediaType is not null && (
                mediaType.StartsWith("application/octet-stream")
                || mediaType.StartsWith("image/")
                || mediaType.StartsWith("audio/") // ✅ explicite audio
                || mediaType.StartsWith("video/") // ✅ explicite video
                || mediaType.StartsWith("application/pdf")
                || mediaType.StartsWith("application/zip")
                || (!isText && !isJson) // tout le reste non-texte/non-json
            ))
            || fileName is not null; // attachment
        var status = (int)resp.StatusCode;
        if (looksBinary)
        {
            byte[] bytes = await resp.Content.ReadAsByteArrayAsync();
            return new ProxyCallResult
            {
                IsBinary = true,
                MimeType = string.IsNullOrWhiteSpace(mediaType) ? "application/octet-stream" : mediaType,
                FileName = fileName,
                Bytes = bytes,
                StatusCode = status
            };
        }

        // Texte / JSON
        string text = await resp.Content.ReadAsStringAsync();
        return new ProxyCallResult { IsBinary = false, MimeType = mediaType ?? "application/json", Text = text, StatusCode = status};
    }
}
