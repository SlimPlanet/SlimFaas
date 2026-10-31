using System.Buffers;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlimFaasMcp.Models;
using Endpoint = SlimFaasMcp.Models.Endpoint;

namespace SlimFaasMcp.Services;

public interface IToolProxyService
{
    Task<List<McpTool>> GetToolsAsync(string swaggerUrl,
        string? baseUrl,
        IDictionary<string, string> additionalHeaders,
        string? mcpPromptB64,
        ushort? slidingExpiration);

    Task<ProxyCallResult> ExecuteToolAsync(
        string swaggerUrl,
        string toolName,
        JsonElement input,
        string? baseUrl = null,
        IDictionary<string, string>? additionalHeaders = null,
        ushort? slidingExpiration = null);
}

public class ToolProxyService(ISwaggerService swaggerService, IHttpClientFactory httpClientFactory, ILogger<ToolProxyService> logger) : IToolProxyService
{
    // OpenAPI ignores header parameters with these names: the request already sets them.
    private static readonly HashSet<string> s_ignoredHeaderParameters =
        new(StringComparer.OrdinalIgnoreCase) { "Accept", "Content-Type", "Authorization" };

    private static readonly SearchValues<char> s_invalidHeaderValueChars = SearchValues.Create("\r\n\0");

    private readonly HttpClient _httpClient = httpClientFactory.CreateClient("InsecureHttpClient");
    private void LogOutgoingRequest(
        HttpMethod method,
        string url,
        IDictionary<string, string>? additionalHeaders,
        string? bodyForLog)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        static string Truncate(string value, int max) =>
            value.Length <= max ? value : value[..max] + "… (truncated)";

        string headersForLog = additionalHeaders is null
            ? string.Empty
            : string.Join(", ", additionalHeaders.Select(h => $"{h.Key}={h.Value}"));

        if (bodyForLog is not null)
        {
            bodyForLog = Truncate(bodyForLog, 4000);
        }

        logger.LogSlimFaasMcpAPIHeadersBody(method, url, headersForLog, bodyForLog ?? "<no body>");
    }

    public async Task<List<McpTool>> GetToolsAsync(string swaggerUrl,
        string? baseUrl,
        IDictionary<string, string>? additionalHeaders,
        string? mcpPromptB64,
        ushort? slidingExpiration)
    {
        JsonDocument swagger = await swaggerService.GetSwaggerAsync(swaggerUrl, baseUrl, additionalHeaders, slidingExpiration);
        IEnumerable<Endpoint> endpoints = swaggerService.ParseEndpoints(swagger);

        List<McpTool> tools = endpoints.Select(e => new McpTool
        {
            Name = e.Name,
            Description = e.Summary,
            InputSchema = McpTool.GenerateInputSchema(e.Parameters ?? []),
            OutputSchema = e.ResponseSchema ?? new JsonObject(),
            Endpoint = new McpTool.EndpointInfo
            {
                Url = CombineBaseUrl(baseUrl, e.Url ?? ""),
                Method = e.Verb,
                ContentType = string.IsNullOrWhiteSpace(e.ContentType) ? GetContentType(e) : e.ContentType
            }
        }).ToList();

        McpPrompt? mcpPrompt = McpPrompt.ParseMcpPrompt(mcpPromptB64);
        if (mcpPrompt != null)
        {
            if (mcpPrompt.ActiveTools != null && mcpPrompt.ActiveTools.Count > 0)
            {
                tools = tools.Where(t => t.Name is not null && mcpPrompt.ActiveTools.Contains(t.Name)).ToList();
            }

            if (mcpPrompt.Tools != null)
            {
                foreach (McpPrompt.McpToolOverride ov in mcpPrompt.Tools)
                {
                    McpTool? tool = tools.FirstOrDefault(t => t.Name == ov.Name);
                    JsonNode? inputSchema = ov.InputSchema;

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
        IDictionary<string, string>? additionalHeaders = null,
        ushort? slidingExpiration = null)
    {
        JsonDocument swagger = await swaggerService.GetSwaggerAsync(swaggerUrl, baseUrl, additionalHeaders, slidingExpiration);
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

        List<Parameter> parameters = endpoint.Parameters ?? [];

        // Path
        string? callUrl = endpoint.Url;
        foreach (Parameter parameter in parameters.Where(p => p.In == "path"))
        {
            callUrl = callUrl?.Replace($"{{{parameter.Name}}}",
                parameter.Name != null && inputDict.TryGetValue(parameter.Name, out JsonElement v) ? v.ToString() : "", StringComparison.Ordinal);
        }

        if (callUrl != null && !callUrl.StartsWith('/'))
        {
            callUrl = "/" + callUrl;
        }

        string fullUrl = baseUrl + callUrl;

        // Query
        IEnumerable<string> queryParams = parameters
            .Where(p => p is { In: "query", Name: not null } && inputDict.ContainsKey(p.Name))
            .Select(p =>
            {
                JsonElement value = inputDict[p.Name!];
                return $"{p.Name}={Uri.EscapeDataString(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText())}";
            });
        if (queryParams.Any())
        {
            fullUrl += "?" + string.Join('&', queryParams);
        }

        // Headers: the headers forwarded by SlimFaasMcp (additionalHeaders) take precedence.
        List<KeyValuePair<string, string>> headerArguments = [];
        foreach (Parameter parameter in parameters.Where(p => p is { In: "header", Name: not null }))
        {
            if (!inputDict.TryGetValue(parameter.Name!, out JsonElement value) ||
                value.ValueKind == JsonValueKind.Null ||
                s_ignoredHeaderParameters.Contains(parameter.Name!) ||
                (additionalHeaders?.Keys.Any(k => string.Equals(k, parameter.Name, StringComparison.OrdinalIgnoreCase)) ?? false))
            {
                continue;
            }

            string headerValue = SerializeHeaderValue(parameter, value);
            if (headerValue.AsSpan().ContainsAny(s_invalidHeaderValueChars))
            {
                return HeaderParameterError($"Header parameter {parameter.Name} cannot contain CR, LF or NUL characters");
            }

            headerArguments.Add(new KeyValuePair<string, string>(parameter.Name!, headerValue));
        }

        // Décision de type de corps
        string? declaredContentType = endpoint.ContentType;
        bool hasFormParams = parameters.Any(p =>
            string.Equals(p.In, "formData", StringComparison.OrdinalIgnoreCase));
        bool hasBinaryBody = parameters.Any(p =>
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
            using HttpRequestMessage reqGet = new(HttpMethod.Get, fullUrl);
            reqGet.Headers.Accept.ParseAdd("application/json");
            if (additionalHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in additionalHeaders)
                {
                    reqGet.Headers.Add(header.Key, header.Value);
                }
            }
            string? rejectedGetHeader = AddHeaderArguments(reqGet, headerArguments);
            if (rejectedGetHeader is not null)
            {
                return UnsupportedHeaderParameterError(rejectedGetHeader);
            }

            LogOutgoingRequest(HttpMethod.Get, fullUrl, additionalHeaders, null);
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
            foreach (Parameter p in parameters.Where(x =>
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
#pragma warning disable CA2000 // the multipart content owns and disposes its parts
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
#pragma warning restore CA2000
            string multipartSummary =
                "multipart/form-data; fields=[" +
                string.Join(", ", parameters
                    .Where(x => string.Equals(x.In, "formData", StringComparison.OrdinalIgnoreCase))
                    .Select(x => x.Name)
                    .Where(n => !string.IsNullOrEmpty(n))) +
                "]";
            LogOutgoingRequest(httpMethod, fullUrl, additionalHeaders, multipartSummary);
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

            string octetSummary = $"application/octet-stream; length={bytes.Length}";
            LogOutgoingRequest(httpMethod, fullUrl, additionalHeaders, octetSummary);
            reqMsg = new HttpRequestMessage(httpMethod, fullUrl) { Content = content };
        }
        else
        {
            // JSON
            StringContent? body = null;
            Parameter? bodyParameter = parameters.FirstOrDefault(p => p.In == "body");
            if (bodyParameter is not null)
            {
                // The body argument is forwarded as-is. Without it, the arguments that are not bound
                // to the path, query or headers are sent as the body object.
                string payload = bodyParameter.Name is not null &&
                                 inputDict.TryGetValue(bodyParameter.Name, out JsonElement bodyElement)
                    ? bodyElement.GetRawText()
                    : JsonSerializer.Serialize(
                        inputDict
                            .Where(kv => !parameters.Any(p => p.In != "body" && p.Name == kv.Key))
                            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
                        AppJsonContext.Default.DictionaryStringJsonElement);

                body = new StringContent(payload, Encoding.UTF8, "application/json");
                LogOutgoingRequest(httpMethod, fullUrl, additionalHeaders, payload);
            }


            reqMsg = new HttpRequestMessage(httpMethod, fullUrl) { Content = body };
        }

        using (reqMsg)
        {
            reqMsg.Headers.Accept.ParseAdd("*/*");
            if (additionalHeaders != null)
            {
                foreach (KeyValuePair<string, string> header in additionalHeaders)
                {
                    reqMsg.Headers.Add(header.Key, header.Value);
                }
            }
            string? rejectedHeader = AddHeaderArguments(reqMsg, headerArguments);
            if (rejectedHeader is not null)
            {
                return UnsupportedHeaderParameterError(rejectedHeader);
            }

            resp = await _httpClient.SendAsync(reqMsg);
            return await ToProxyCallResult(resp);
        }
    }

    private static string CombineBaseUrl(string? baseUrl, string endpointUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return endpointUrl;
        }

        if (endpointUrl.StartsWith("http", StringComparison.Ordinal))
        {
            return endpointUrl;
        }

        return baseUrl.TrimEnd('/') + "/" + endpointUrl.TrimStart('/');
    }

    // Returns the name of the first header argument that the request cannot carry, or null when all were added.
    private static string? AddHeaderArguments(HttpRequestMessage request, List<KeyValuePair<string, string>> headerArguments)
    {
        foreach ((string name, string value) in headerArguments)
        {
            // Content headers such as Content-Language belong to the body, so they need one.
            if (!request.Headers.TryAddWithoutValidation(name, value) &&
                request.Content?.Headers.TryAddWithoutValidation(name, value) != true)
            {
                return name;
            }
        }

        return null;
    }

    // OpenAPI serializes header parameters with the "simple" style: an array [1,2] becomes "1,2", and an object
    // {"role":"admin","id":5} becomes "role,admin,id,5", or "role=admin,id=5" with explode. A parameter described
    // with content application/json is sent as JSON.
    private static string SerializeHeaderValue(Parameter parameter, JsonElement value)
    {
        if (parameter.IsJsonContent)
        {
            return JsonSerializer.Serialize(value, AppJsonContext.Default.JsonElement);
        }

        static string Primitive(JsonElement element) =>
            element.ValueKind == JsonValueKind.String
                ? element.GetString()!
                : JsonSerializer.Serialize(element, AppJsonContext.Default.JsonElement);

        return value.ValueKind switch
        {
            JsonValueKind.Array => string.Join(',', value.EnumerateArray().Select(Primitive)),
            JsonValueKind.Object => string.Join(',', value.EnumerateObject().Select(property =>
                parameter.Explode
                    ? $"{property.Name}={Primitive(property.Value)}"
                    : $"{property.Name},{Primitive(property.Value)}")),
            _ => Primitive(value)
        };
    }

    private static ProxyCallResult UnsupportedHeaderParameterError(string name) =>
        HeaderParameterError($"Header parameter {name} cannot be sent with this request");

    private static ProxyCallResult HeaderParameterError(string message) =>
        new()
        {
            IsBinary = false,
            Text = JsonSerializer.Serialize(new JsonObject { ["error"] = message }, AppJsonContext.Default.JsonNode),
            MimeType = "application/json",
            StatusCode = 400
        };

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
                      (mediaType == "application/json" || mediaType.EndsWith("+json", StringComparison.Ordinal));
        bool isText = mediaType is not null && mediaType.StartsWith("text/", StringComparison.Ordinal);

        // Heuristique "binaire" explicite + fallback générique
        bool looksBinary =
            (mediaType is not null && (
                mediaType.StartsWith("application/octet-stream", StringComparison.Ordinal)
                || mediaType.StartsWith("image/", StringComparison.Ordinal)
                || mediaType.StartsWith("audio/", StringComparison.Ordinal) // ✅ explicite audio
                || mediaType.StartsWith("video/", StringComparison.Ordinal) // ✅ explicite video
                || mediaType.StartsWith("application/pdf", StringComparison.Ordinal)
                || mediaType.StartsWith("application/zip", StringComparison.Ordinal)
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
