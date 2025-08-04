using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using SlimFaasMcp;
using SlimFaasMcp.Services;
using SlimFaasMcp.Models;

var builder = WebApplication.CreateSlimBuilder(args);

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

app.MapGet("/mcp", () => Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

/* -------------------------------------------------------------------------
 * Endpoint MCP JSON-RPC 2.0 (POST /mcp)
 * --------------------------------------------------------------------- */
app.MapPost("/mcp", async (HttpRequest httpRequest,
                           IToolProxyService toolProxyService) =>
{
    var authHeader = httpRequest.Headers["Authorization"].FirstOrDefault();
    using var jsonDocument = await JsonDocument.ParseAsync(httpRequest.Body);
    var root = jsonDocument.RootElement;

    // --- query-string --------------------------------------------------
    var qs            = httpRequest.Query;
    var openapiUrl    = qs.TryGetValue("openapi_url", out var qurl)   ? qurl.ToString()   : "";
    var baseUrl       = qs.TryGetValue("base_url", out var qb)       ? qb.ToString()     : "";
    var mcpPromptB64  = qs.TryGetValue("mcp_prompt", out var qp)     ? qp.ToString()     : null;

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
            response["result"] = new JsonObject {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"]    = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"]      = new JsonObject {
                    ["name"]    = ".NET MCP Demo",
                    ["version"] = "0.1.0"
                }
            };
            break;

        /* 2. tools/list ------------------------------------------------ */
        case "tools/list":
        {
            var tools = await toolProxyService.GetToolsAsync(
                openapiUrl, baseUrl, authHeader, mcpPromptB64);

            response["result"] = new JsonObject {
                ["tools"] = new JsonArray(tools.Select(t => new JsonObject {
                    ["name"]        = t.Name,
                    ["title"]       = t.Description,
                    ["description"] = t.Description,
                    ["inputSchema"] = JsonNode.Parse(
                        JsonSerializer.Serialize(t.InputSchema, AppJsonContext.Default.JsonNode)),
                    ["outputSchema"] = JsonNode.Parse(JsonSerializer
                        .Serialize(t.OutputSchema, AppJsonContext.Default.JsonNode))
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

            var resultStr = await toolProxyService.ExecuteToolAsync(
                                openapiUrl,
                                p.GetProperty("name").GetString()!,
                                p.GetProperty("arguments"),
                                baseUrl,
                                authHeader);

            // Tout est déjà string → aucun YAML, aucune réflexion
            response["result"] = new JsonObject {
                ["content"] = new JsonArray {
                    new JsonObject {
                        ["type"] = "text",
                        ["text"] = $"```json\n{resultStr}\n```"
                    }
                }
            };
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
        HttpRequest req,
        IToolProxyService proxy)
        => TypedResults.Ok(await proxy.GetToolsAsync(
                openapi_url, base_url,
                req.Headers.Authorization.FirstOrDefault(),
                mcp_prompt)));

grp.MapPost("/{toolName}", async Task<Ok<string>> (
        string toolName,
        string openapi_url,
        [FromBody] JsonElement arguments,
        [FromQuery] string? base_url,
        [FromQuery] string? mcp_prompt,
        HttpRequest req,
        IToolProxyService proxy)
        => TypedResults.Ok(await proxy.ExecuteToolAsync(
                openapi_url, toolName, arguments,
                base_url,
                req.Headers.Authorization.FirstOrDefault())));

app.MapGet("/health", () => Results.Text("OK"));

await app.RunAsync();

public partial class Program { }
