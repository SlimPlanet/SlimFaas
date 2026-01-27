
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SlimFaasMcpGateway.Auth;
using SlimFaasMcpGateway.Data;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Services;

public interface IMcpDiscoveryService
{
    Task<string> LoadCatalogYamlAsync(Guid configurationId, CancellationToken ct);
}

public sealed class McpDiscoveryService : IMcpDiscoveryService
{
    private readonly GatewayDbContext _db;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _protector;

    public McpDiscoveryService(GatewayDbContext db, IHttpClientFactory httpClientFactory, ISecretProtector protector)
    {
        _db = db;
        _httpClientFactory = httpClientFactory;
        _protector = protector;
    }

    public async Task<string> LoadCatalogYamlAsync(Guid configurationId, CancellationToken ct)
    {
        var cfg = await _db.Configurations.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == configurationId, ct);
        if (cfg is null || cfg.IsDeleted) throw new ApiException(404, "Configuration not found.");

        // Load upstream servers
        var upstreams = await _db.UpstreamServers
            .Where(x => x.ConfigurationId == configurationId)
            .OrderBy(x => x.DisplayOrder)
            .ThenBy(x => x.ToolPrefix)
            .ToListAsync(ct);

        // Fallback to legacy single URL if no upstreams configured
        if (upstreams.Count == 0 && !string.IsNullOrWhiteSpace(cfg.UpstreamMcpUrl))
        {
            InputValidators.ValidateAbsoluteHttpUrl(cfg.UpstreamMcpUrl, "UpstreamMcpUrl");

            string? token = null;
            if (!string.IsNullOrWhiteSpace(cfg.DiscoveryJwtTokenProtected))
            {
                try { token = _protector.Unprotect(cfg.DiscoveryJwtTokenProtected); }
                catch { token = null; }
            }

            return await FetchSingleUpstreamCatalogAsync(cfg.UpstreamMcpUrl, token, "", ct);
        }

        // Multi-upstream mode: fetch and merge catalogs
        return await FetchAndMergeCatalogsAsync(upstreams, ct);
    }

    private async Task<string> FetchAndMergeCatalogsAsync(List<Data.Entities.UpstreamMcpServer> upstreams, CancellationToken ct)
    {
        var allTools = new List<(string prefix, JsonElement data)>();
        var allResources = new List<(string prefix, JsonElement data)>();
        var allPrompts = new List<(string prefix, JsonElement data)>();

        foreach (var upstream in upstreams)
        {
            InputValidators.ValidateAbsoluteHttpUrl(upstream.BaseUrl, $"Upstream[{upstream.ToolPrefix}].BaseUrl");

            string? token = null;
            if (!string.IsNullOrWhiteSpace(upstream.DiscoveryJwtTokenProtected))
            {
                try { token = _protector.Unprotect(upstream.DiscoveryJwtTokenProtected); }
                catch { token = null; }
            }

            var http = _httpClientFactory.CreateClient("upstream");


            // Fetch MCP methods for this upstream
            var tools = await FetchMcpMethodAsync(http, upstream.BaseUrl, token, "tools/list", ct);
            var resources = await FetchMcpMethodAsync(http, upstream.BaseUrl, token, "resources/list", ct);
            var prompts = await FetchMcpMethodAsync(http, upstream.BaseUrl, token, "prompts/list", ct);

            if (tools.HasValue) allTools.Add((upstream.ToolPrefix, tools.Value));
            if (resources.HasValue) allResources.Add((upstream.ToolPrefix, resources.Value));
            if (prompts.HasValue) allPrompts.Add((upstream.ToolPrefix, prompts.Value));
        }

        // Build merged YAML catalog
        var sb = new StringBuilder();
        sb.AppendLine("# MCP catalog discovery - merged from multiple upstreams");
        sb.AppendLine("# Tool prefixes are added to avoid conflicts");
        sb.AppendLine();

        if (allTools.Count > 0)
        {
            sb.AppendLine("tools:");
            foreach (var (prefix, data) in allTools)
            {
                sb.AppendLine($"  # From upstream: {prefix}");
                AppendYamlFromJsonWithPrefix(sb, data, prefix, 2);
            }
        }

        if (allResources.Count > 0)
        {
            sb.AppendLine("resources:");
            foreach (var (prefix, data) in allResources)
            {
                sb.AppendLine($"  # From upstream: {prefix}");
                AppendYamlFromJsonWithPrefix(sb, data, prefix, 2);
            }
        }

        if (allPrompts.Count > 0)
        {
            sb.AppendLine("prompts:");
            foreach (var (prefix, data) in allPrompts)
            {
                sb.AppendLine($"  # From upstream: {prefix}");
                AppendYamlFromJsonWithPrefix(sb, data, prefix, 2);
            }
        }

        sb.AppendLine();
        sb.AppendLine("# Optional override structure (supported by gateway):");
        sb.AppendLine("# tools:");
        sb.AppendLine("#   allow:");
        sb.AppendLine("#     - prefix_tool_name");
        sb.AppendLine("#   overrides:");
        sb.AppendLine("#     prefix_tool_name:");
        sb.AppendLine("#       description: \"New description\"");

        return sb.ToString();
    }

    private async Task<string> FetchSingleUpstreamCatalogAsync(string baseUrl, string? token, string prefix, CancellationToken ct)
    {
        var http = _httpClientFactory.CreateClient("upstream");


        var tools = await FetchMcpMethodAsync(http, baseUrl, token, "tools/list", ct);
        var resources = await FetchMcpMethodAsync(http, baseUrl, token, "resources/list", ct);
        var prompts = await FetchMcpMethodAsync(http, baseUrl, token, "prompts/list", ct);

        var sb = new StringBuilder();
        sb.AppendLine("# MCP catalog discovery (editable)");
        sb.AppendLine("# Methods that returned 404 or 'method not found' are omitted");
        sb.AppendLine();

        if (tools.HasValue)
        {
            sb.AppendLine("tools:");
            AppendYamlFromJson(sb, tools.Value, 2);
        }

        if (resources.HasValue)
        {
            sb.AppendLine("resources:");
            AppendYamlFromJson(sb, resources.Value, 2);
        }

        if (prompts.HasValue)
        {
            sb.AppendLine("prompts:");
            AppendYamlFromJson(sb, prompts.Value, 2);
        }

        sb.AppendLine();
        sb.AppendLine("# Optional override structure (supported by gateway):");
        sb.AppendLine("# tools:");
        sb.AppendLine("#   allow:");
        sb.AppendLine("#     - tool_name");
        sb.AppendLine("#   overrides:");
        sb.AppendLine("#     tool_name:");
        sb.AppendLine("#       description: \"New description\"");

        return sb.ToString();
    }

    private async Task<JsonElement?> FetchMcpMethodAsync(HttpClient http, string baseUrl, string? token, string method, CancellationToken ct)
    {
        try
        {
            var url = baseUrl;

            // Build JSON-RPC 2.0 request
            var jsonRpcRequest = new
            {
                jsonrpc = "2.0",
                id = Guid.NewGuid().ToString(),
                method = method,
                @params = new { }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url);
            req.Content = new StringContent(
                JsonSerializer.Serialize(jsonRpcRequest),
                Encoding.UTF8,
                "application/json"
            );

            if (!string.IsNullOrWhiteSpace(token))
                req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

            using var res = await http.SendAsync(req, ct);
            var body = await res.Content.ReadAsStringAsync(ct);

            // If 404, the method is not supported - return null instead of failing
            if (res.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!res.IsSuccessStatusCode)
                throw new ApiException(502, $"Upstream MCP call failed for '{method}': {(int)res.StatusCode} {res.ReasonPhrase} - {body}");

            // Parse JSON-RPC response and extract result
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;

                // Check for JSON-RPC error
                if (root.TryGetProperty("error", out var error))
                {
                    var errorCode = error.TryGetProperty("code", out var code) ? code.GetInt32() : 0;
                    var errorMsg = error.TryGetProperty("message", out var msg) ? msg.GetString() : "Unknown error";

                    // JSON-RPC error code -32601 = Method not found
                    if (errorCode == -32601)
                    {
                        return null;
                    }

                    throw new ApiException(502, $"Upstream MCP error for '{method}': {errorMsg}");
                }

                // Extract result and clone it before doc is disposed
                if (root.TryGetProperty("result", out var result))
                {
                    return result.Clone();
                }

                throw new ApiException(502, $"Invalid JSON-RPC response for '{method}': missing 'result' field");
            }
            catch (JsonException ex)
            {
                throw new ApiException(502, $"Failed to parse MCP response for '{method}': {ex.Message}");
            }
        }
        catch (ApiException)
        {
            throw; // Re-throw API exceptions (actual errors)
        }
        catch (Exception ex)
        {
            // Log but don't fail for individual method - might not be supported
            // In production, you'd log this properly
            return null;
        }
    }

    private static void AppendYamlFromJsonWithPrefix(StringBuilder sb, JsonElement element, string prefix, int indent)
    {
        var indentStr = new string(' ', indent);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    // Add prefix to tool/resource/prompt names
                    var key = prop.Name;
                    if (!string.IsNullOrEmpty(prefix) && (key == "name" || key == "uri"))
                    {
                        sb.Append(indentStr).Append(prop.Name).Append(": ");
                        if (prop.Value.ValueKind == JsonValueKind.String)
                        {
                            var value = prop.Value.GetString() ?? "";
                            sb.AppendLine($"\"{prefix}{value}\"");
                        }
                        else
                        {
                            sb.AppendLine(FormatYamlValue(prop.Value));
                        }
                    }
                    else
                    {
                        sb.Append(indentStr).Append(prop.Name).Append(": ");
                        if (prop.Value.ValueKind == JsonValueKind.Object)
                        {
                            sb.AppendLine();
                            AppendYamlFromJsonWithPrefix(sb, prop.Value, prefix, indent + 2);
                        }
                        else if (prop.Value.ValueKind == JsonValueKind.Array)
                        {
                            sb.AppendLine();
                            AppendYamlFromJsonWithPrefix(sb, prop.Value, prefix, indent + 2);
                        }
                        else
                        {
                            sb.AppendLine(FormatYamlValue(prop.Value));
                        }
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    sb.Append(indentStr).Append("- ");
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        sb.AppendLine();
                        AppendYamlFromJsonWithPrefix(sb, item, prefix, indent + 2);
                    }
                    else if (item.ValueKind == JsonValueKind.Array)
                    {
                        sb.AppendLine();
                        AppendYamlFromJsonWithPrefix(sb, item, prefix, indent + 2);
                    }
                    else
                    {
                        sb.AppendLine(FormatYamlValue(item));
                    }
                }
                break;

            default:
                sb.Append(indentStr).AppendLine(FormatYamlValue(element));
                break;
        }
    }

    private static void AppendYamlFromJson(StringBuilder sb, JsonElement element, int indent)
    {
        var indentStr = new string(' ', indent);

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    sb.Append(indentStr).Append(prop.Name).Append(": ");
                    if (prop.Value.ValueKind == JsonValueKind.Object)
                    {
                        sb.AppendLine();
                        AppendYamlFromJson(sb, prop.Value, indent + 2);
                    }
                    else if (prop.Value.ValueKind == JsonValueKind.Array)
                    {
                        sb.AppendLine();
                        AppendYamlFromJson(sb, prop.Value, indent + 2);
                    }
                    else
                    {
                        sb.AppendLine(FormatYamlValue(prop.Value));
                    }
                }
                break;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    sb.Append(indentStr).Append("- ");
                    if (item.ValueKind == JsonValueKind.Object)
                    {
                        sb.AppendLine();
                        AppendYamlFromJson(sb, item, indent + 2);
                    }
                    else if (item.ValueKind == JsonValueKind.Array)
                    {
                        sb.AppendLine();
                        AppendYamlFromJson(sb, item, indent + 2);
                    }
                    else
                    {
                        sb.AppendLine(FormatYamlValue(item));
                    }
                }
                break;

            default:
                sb.Append(indentStr).AppendLine(FormatYamlValue(element));
                break;
        }
    }

    private static string FormatYamlValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => $"\"{element.GetString()?.Replace("\"", "\\\"")}\"",
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.ToString()
        };
    }
}
