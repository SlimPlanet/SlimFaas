using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SlimFaasMcp;
using SlimFaasMcp.Configuration;
using SlimFaasMcp.Extensions;
using SlimFaasMcp.Services;
using SlimFaasMcp.Models;

var builder = WebApplication.CreateSlimBuilder(args);

var corsSection  = builder.Configuration.GetSection("Cors");
var corsSettings = corsSection.Get<CorsSettings>() ?? new CorsSettings();
builder.Services.Configure<CorsSettings>(corsSection);

// ---- Flat env vars override (CORS_*) ----
static string[]? ReadCsv(string? s) =>
    string.IsNullOrWhiteSpace(s) ? null :
        s.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

string? envOrigins = builder.Configuration["CORS_ORIGINS"];
string? envMethods = builder.Configuration["CORS_METHODS"];
string? envHeaders = builder.Configuration["CORS_HEADERS"];
string? envExpose  = builder.Configuration["CORS_EXPOSE"];
string? envCreds   = builder.Configuration["CORS_CREDENTIALS"];
string? envMaxAge  = builder.Configuration["CORS_MAXAGEMINUTES"];

var o = ReadCsv(envOrigins); if (o is not null) corsSettings.Origins  = o;
var m = ReadCsv(envMethods); if (m is not null) corsSettings.Methods  = m;
var h = ReadCsv(envHeaders); if (h is not null) corsSettings.Headers  = h;
var e = ReadCsv(envExpose);  if (e is not null) corsSettings.Expose   = e;
if (bool.TryParse(envCreds, out var bc)) corsSettings.Credentials = bc;
if (int.TryParse(envMaxAge, out var mi)) corsSettings.MaxAgeMinutes = mi;

var openTelemetryConfig = builder.Configuration
    .GetSection("OpenTelemetry")
    .Get<OpenTelemetryConfig>() ?? new OpenTelemetryConfig();

builder.Services.AddOpenTelemetry(openTelemetryConfig);

// Register CORS policy using the already present ConfigureCorsPolicyFromWildcard(...)
builder.Services.AddCors(options =>
{
    options.AddPolicy("SlimFaasMcpCors", policy =>
    {
        ConfigureCorsPolicyFromWildcard(policy, corsSettings);
    });
});

builder.Services.AddHttpClient("InsecureHttpClient")
                .ConfigurePrimaryHttpMessageHandler(() =>
                    new HttpClientHandler {
                        ServerCertificateCustomValidationCallback =
                            HttpClientHandler.DangerousAcceptAnyServerCertificateValidator
                    });

builder.Services.AddSingleton<ISwaggerService, SwaggerService>();
builder.Services.AddSingleton<IToolProxyService, ToolProxyService>();
builder.Services.AddMemoryCache();

// 👉 force ASP.NET à utiliser notre JsonSerializerContext
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors("SlimFaasMcpCors");

app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

/* -------------------------------------------------------------------------
 * Endpoint MCP JSON-RPC 2.0 (POST /mcp)
 * --------------------------------------------------------------------- */
app.MapPost("/mcp", async (HttpRequest httpRequest,
                           IToolProxyService toolProxyService) =>
{
    /* ---------- utilitaire qui fabrique le challenge OAuth ---------- */
    static IResult Challenge(HttpRequest req, string oauthB64)
    {
        var metaUrl = $"https://{req.Host}/{oauthB64}/.well-known/oauth-protected-resource";

        // On écrit directement l’en-tête
        req.HttpContext.Response.Headers["WWW-Authenticate"] =
            $"Bearer resource_metadata=\"{metaUrl}\"";

        // Puis on renvoie simplement le code 401
        return Results.StatusCode(StatusCodes.Status401Unauthorized);
    }

    IDictionary<string, string> additionalHeaders = AuthHeader(httpRequest, out string? authHeader);

    using var jsonDocument = await JsonDocument.ParseAsync(httpRequest.Body);
    var root = jsonDocument.RootElement;

    // --- query-string --------------------------------------------------
    var qs            = httpRequest.Query;
    var openapiUrl    = qs.TryGetValue("openapi_url", out var qurl)   ? qurl.ToString()   : "";
    var baseUrl       = qs.TryGetValue("base_url", out var qb)       ? qb.ToString()     : "";
    var mcpPromptB64  = qs.TryGetValue("mcp_prompt", out var qp)     ? qp.ToString()     : null;
    var oauthB64    = qs.TryGetValue("oauth", out var qOauth) ? qOauth.ToString() : null;
    var toolPrefix = qs.TryGetValue("tool_prefix", out var qtp) ? qtp.ToString() : null;
    var expirationTime = qs.TryGetValue("cache_expiration", out var exp) ? exp.ToString() : null;

    var structuredContentEnabled =
        qs.TryGetValue("structured_content", out var qsc)
        && string.Equals(qsc.ToString(), "true", StringComparison.OrdinalIgnoreCase);


    if (!string.IsNullOrEmpty(oauthB64) && string.IsNullOrWhiteSpace(authHeader))
        return Challenge(httpRequest, oauthB64);

    ushort? slidingExpiration = ushort.TryParse(expirationTime, out ushort result) ?  result : null;

    // --- champs JSON-RPC ----------------------------------------------
    JsonNode response = new JsonObject {
        ["jsonrpc"] = root.GetProperty("jsonrpc").GetString() ?? "2.0"
    };
    if (root.TryGetProperty("id", out var idElem))
        response["id"] = JsonNode.Parse(idElem.GetRawText())!;

    switch (root.GetProperty("method").GetString())
    {
        /* 1. initialize ------------------------------------------------ */
        case "initialize":
            response["result"] = new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "SlimFaas MCP", ["version"] = "0.1.0" }
            };
            break;

        /* 2. tools/list ------------------------------------------------ */
        case "tools/list":
            {
                var tools = await toolProxyService.GetToolsAsync(
                    openapiUrl, baseUrl, additionalHeaders, mcpPromptB64, slidingExpiration);
                if (!string.IsNullOrWhiteSpace(toolPrefix))
                {
                    foreach (var t in tools)
                        t.Name = $"{toolPrefix}_{t.Name}";
                }
                response["result"] = new JsonObject
                {
                    ["tools"] = new JsonArray(tools.Select(t =>
                    {
                        var node = new JsonObject
                        {
                            ["name"]        = t.Name,
                            ["description"] = t.Description,
                            ["inputSchema"] = JsonNode.Parse(
                                JsonSerializer.Serialize(t.InputSchema, AppJsonContext.Default.JsonNode))!
                        };

                        if (structuredContentEnabled && HasKnownOutputSchema(t.OutputSchema))
                        {
                            var wrapped = OutputSchemaWrapper.WrapForStructuredContent(t.OutputSchema);
                            if(wrapped != null)
                                node["outputSchema"] = JsonNode.Parse(
                                    JsonSerializer.Serialize(wrapped, AppJsonContext.Default.JsonNode))!;
                        }

                        return node;
                    }).ToArray())
                };
                break;
            }

        /* 3. tools/call ------------------------------------------------ */
        case "tools/call":
            {
                if (!root.TryGetProperty("params", out var p))
                {
                    response["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Missing params" };
                    break;
                }
                var incomingName = p.GetProperty("name").GetString()!;
                var realName = StripToolPrefix(incomingName, toolPrefix);

                // 🔁 Récupère la même liste d'outils que pour tools/list (avec mcpPrompt & cache)
                var toolsForCall = await toolProxyService.GetToolsAsync(openapiUrl, baseUrl, additionalHeaders, mcpPromptB64, slidingExpiration);
                var toolMeta     = toolsForCall.FirstOrDefault(t => string.Equals(t.Name, realName, StringComparison.Ordinal));

                var callResult = await toolProxyService.ExecuteToolAsync(
                    openapiUrl,
                    realName!,
                    p.GetProperty("arguments"),
                    baseUrl,
                    additionalHeaders);

                bool allowStructured = structuredContentEnabled && toolMeta is not null && HasKnownOutputSchema(toolMeta.OutputSchema) && callResult.StatusCode >= 200
                    && callResult.StatusCode < 300;

                // ✅ RESULT MCP (content[] + structuredContent si activé via query)
                var resultObj = McpContentBuilder.BuildResult(callResult, allowStructured);

                response["result"] = resultObj;

                break;
            }
        default:
            response["error"] = new JsonObject {
                ["code"] = -32601,
                ["message"] = "Method not found"
            };
            break;
    }

    return Results.Json(
        response,
        AppJsonContext.Default.JsonNode);
});

/* -------------------------------------------------------------------------
 * Minimal APIs Swagger → Tools → Execution
 * --------------------------------------------------------------------- */
var grp = app.MapGroup("/tools");

grp.MapGet("/", async Task<Ok<List<McpTool>>> (
        string openapi_url,
        [FromQuery] string? base_url,
        [FromQuery] string? mcp_prompt,
        [FromQuery] ushort? cache_expiration,
        HttpRequest httpRequest,
        IToolProxyService proxy)
        =>
{
    IDictionary<string, string> additionalHeaders = AuthHeader(httpRequest, out string? authHeader);

    var tools = await proxy.GetToolsAsync(
        openapi_url, base_url,
        additionalHeaders,
        mcp_prompt,
        cache_expiration);

    var qs = httpRequest.Query;
    var toolPrefix = qs.TryGetValue("tool_prefix", out var qtp) ? qtp.ToString() : null;

    if (!string.IsNullOrWhiteSpace(toolPrefix))
    {
        foreach (var t in tools)
            t.Name = $"{toolPrefix}_{t.Name}";
    }
    var structuredContentEnabled =
        qs.TryGetValue("structured_content", out var qsc)
        && string.Equals(qsc.ToString(), "true", StringComparison.OrdinalIgnoreCase);

    if (!structuredContentEnabled)
    {
        foreach (var t in tools) t.OutputSchema = new JsonObject();
    }
    else
    {
        // ⬇️ applique le même wrapping pour que l’UI annonce le bon schéma
        foreach (var t in tools)
            if (HasKnownOutputSchema(t.OutputSchema))
                t.OutputSchema = OutputSchemaWrapper.WrapForStructuredContent(t.OutputSchema);
            else
                t.OutputSchema = new JsonObject();
    }

    return TypedResults.Ok(tools);
});

grp.MapPost("/{toolName}", async Task<IResult> (
        string toolName,
        string openapi_url,
        [FromBody] JsonElement arguments,
        [FromQuery] string? base_url,
        [FromQuery] string? mcp_prompt,
        [FromQuery] ushort? cache_expiration,
        HttpRequest httpRequest,
        IToolProxyService proxy)
    =>
{
    IDictionary<string, string> additionalHeaders = AuthHeader(httpRequest, out string? authHeader);
    var qs = httpRequest.Query;
    var toolPrefix = qs.TryGetValue("tool_prefix", out var qtp) ? qtp.ToString() : null;
    var realName   = StripToolPrefix(toolName, toolPrefix);
    var callResult = await proxy.ExecuteToolAsync(
        openapi_url, realName!, arguments,
        base_url,
        additionalHeaders);

    var structuredContentEnabled =
        qs.TryGetValue("structured_content", out var qsc)
        && string.Equals(qsc.ToString(), "true", StringComparison.OrdinalIgnoreCase);
    var toolsForCall = await proxy.GetToolsAsync(openapi_url, base_url, additionalHeaders, mcp_prompt, cache_expiration);
    var toolMeta     = toolsForCall.FirstOrDefault(t => string.Equals(t.Name, realName, StringComparison.Ordinal));
    bool allowStructured = structuredContentEnabled && toolMeta is not null && HasKnownOutputSchema(toolMeta.OutputSchema) && callResult.StatusCode >= 200
        && callResult.StatusCode < 300;


    var resultObj = McpContentBuilder.BuildResult(callResult, allowStructured);
    return Results.Json(resultObj, AppJsonContext.Default.JsonNode);
});



app.MapGet("/{oauth?}/.well-known/oauth-protected-resource",
    (string? oauth,
        HttpRequest req) =>
    {

        if (string.IsNullOrWhiteSpace(oauth))
            return Results.NotFound();

        try
        {
            var json = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(oauth));
            var meta = System.Text.Json.JsonSerializer.Deserialize(
                           json,
                           AppJsonContext.Default.OAuthProtectedResourceMetadata)
                       ?? throw new Exception("JSON vide");

            return Results.Json(meta, AppJsonContext.Default.OAuthProtectedResourceMetadata);
        }
        catch (Exception ex)
        {
            return Results.BadRequest($"Paramètre oauth invalide : {ex.Message}");
        }
    });


app.MapGet("/health", () => Results.Text("OK"));

await app.RunAsync();

IDictionary<string, string> AuthHeader(HttpRequest httpRequest1, out string? s)
{
    s = httpRequest1.Headers["Authorization"].FirstOrDefault();
    var dpopHeader = httpRequest1.Headers["Dpop"].FirstOrDefault();

    IDictionary<string, string> dictionary = new Dictionary<string, string>();
    if (!string.IsNullOrWhiteSpace(s))
        dictionary["Authorization"] = s;
    if (!string.IsNullOrWhiteSpace(dpopHeader))
        dictionary["Dpop"] = dpopHeader;
    return dictionary;
}

static string? StripToolPrefix(string? name, string? prefix)
{
    if (string.IsNullOrWhiteSpace(prefix) || string.IsNullOrWhiteSpace(name)) return name;
    var wanted = prefix + "_";
    return name!.StartsWith(wanted, StringComparison.OrdinalIgnoreCase)
        ? name.Substring(wanted.Length)
        : name;
}

static void ConfigureCorsPolicyFromWildcard(CorsPolicyBuilder policy, CorsSettings cfg)
{
    // ORIGINS
    if (IsAny(cfg.Origins))
    {
        if (cfg.Credentials)
        {
            // Echo dynamique de toute origine (⚠️ à n'activer qu'en connaissance de cause)
            policy.SetIsOriginAllowed(_ => true).AllowCredentials();
        }
        else
        {
            policy.AllowAnyOrigin();
        }
    }
    else
    {
        var originPred = BuildOriginPredicate(cfg.Origins!);
        policy.SetIsOriginAllowed(originPred);

        if (cfg.Credentials) policy.AllowCredentials();
        else                  policy.DisallowCredentials();
    }

    // METHODS
    if (IsAny(cfg.Methods)) policy.AllowAnyMethod();
    else                    policy.WithMethods(Normalize(cfg.Methods!));

    // HEADERS
    if (IsAny(cfg.Headers)) policy.AllowAnyHeader();
    else                    policy.WithHeaders(Normalize(cfg.Headers!));

    // EXPOSED
    if (cfg.Expose is { Length: > 0 }) policy.WithExposedHeaders(Normalize(cfg.Expose));

    // PREFLIGHT CACHE
    if (cfg.MaxAgeMinutes is int m && m > 0) policy.SetPreflightMaxAge(TimeSpan.FromMinutes(m));
}

static bool IsAny(string[]? arr) => arr is null || arr.Length == 0 || Array.Exists(arr, s => s.Trim() == "*");
static string[] Normalize(string[] arr) => arr.Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)).ToArray();

static Func<string, bool> BuildOriginPredicate(string[] patterns)
{
    // Supporte:
    //   https://*.axa.com
    //   http://localhost:*
    //   http*://dev-*.example.local:808*
    //   https://exact.example.com
    var regs = patterns
        .Select(p => p.Trim())
        .Where(p => !string.IsNullOrWhiteSpace(p))
        .Select(p => new Regex("^" + GlobToRegex(p) + "$", RegexOptions.IgnoreCase | RegexOptions.Compiled))
        .ToArray();

    if (regs.Length == 0)
        return _ => false;

    return origin =>
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;
        foreach (var r in regs) if (r.IsMatch(origin)) return true;
        return false;
    };
}

static string GlobToRegex(string pattern)
{
    // Échappe les regex chars, puis remplace les jokers glob par des classes regex
    // *  => .*
    // ?  => .
    // On garde : // : // dans l'URL ne posent pas souci car on matche toute la chaîne
    var special = new HashSet<char> { '.', '$', '^', '{', '[', '(', '|', ')', '+', '\\' };
    var sb = new System.Text.StringBuilder(pattern.Length * 2);
    foreach (var ch in pattern)
    {
        switch (ch)
        {
            case '*': sb.Append(".*"); break;
            case '?': sb.Append('.');  break;
            default:
                if (special.Contains(ch)) sb.Append('\\');
                sb.Append(ch);
                break;
        }
    }
    return sb.ToString();
}

static bool HasKnownOutputSchema(System.Text.Json.Nodes.JsonNode? schema)
{
    if (schema is not System.Text.Json.Nodes.JsonObject obj) return false;

    // cas explicite: type string non vide
    if (obj.TryGetPropertyValue("type", out var t) && t is System.Text.Json.Nodes.JsonValue tv)
    {
        var ts = tv.TryGetValue<string>(out var s) ? s : tv.ToString();
        if (!string.IsNullOrWhiteSpace(ts)) return true;
    }

    // cas implicites: présence de structure/combinators
    if (obj.ContainsKey("properties")) return true;
    if (obj.ContainsKey("items")) return true;
    if (obj.ContainsKey("anyOf") || obj.ContainsKey("oneOf") || obj.ContainsKey("allOf")) return true;

    return false;
}


public partial class Program { }
