using System.Text.Json;
using System.Text.Json.Nodes;
using SlimFaasMcp.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddSingleton<SwaggerService>();
builder.Services.AddSingleton<ToolProxyService>();
builder.Services.AddSingleton<ToolProxyService>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/mcp", () =>
    Results.StatusCode(StatusCodes.Status405MethodNotAllowed));


/* -------------------------------------------------------------------------
 * 2. Endpoint MCP JSON-RPC 2.0  (POST /mcp)
 * --------------------------------------------------------------------- */
app.MapPost("/mcp", async (HttpRequest req,
                           SwaggerService swaggerSvc,
                           ToolProxyService proxySvc) =>
{
    using var doc = await JsonDocument.ParseAsync(req.Body);
    var root = doc.RootElement;

    /* 1️⃣  Lis les query-string ---------------------- */
    var qs          = req.Query;
    var openapiUrl       = qs.TryGetValue("openapi_url", out var qurl)      ? qurl.ToString()      : "";
    var baseUrl      = qs.TryGetValue("base_url", out var qbase)? qbase.ToString()     : "";

    /* --- champs JSON-RPC de base -------------------------------------- */
    string jsonrpc = root.GetProperty("jsonrpc").GetString() ?? "2.0";
    bool   hasId   = root.TryGetProperty("id", out var idElem);
    string method  = root.GetProperty("method").GetString() ?? string.Empty;

    var resp = new JsonObject { ["jsonrpc"] = jsonrpc };
    if (hasId)
    {
        // Convertit le JsonElement (nombre, chaîne, etc.) en JsonNode
        resp["id"] = JsonNode.Parse(idElem.GetRawText())!;
    }

    switch (method)
    {
        /* ---------------------------------------------------------------
         * 1. initialize
         * ------------------------------------------------------------- */
        case "initialize":
            resp["result"] = new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",     // la date de version MCP que vous supportez
                ["capabilities"]    = new JsonObject    // objet vide(s) pour chaque feature
                {
                    ["tools"] = new JsonObject()        // <= indique « je parle MCP-Tools »
                },
                ["serverInfo"]      = new JsonObject
                {
                    ["name"]    = ".NET MCP Demo",
                    ["version"] = "0.1.0"
                }
            };
            break;

        /* ---------------------------------------------------------------
         * 2. tools/list
         *    - charge dynamiquement un Swagger distant (params.url)
         *    - option base_url pour override
         * ------------------------------------------------------------- */
        case "tools/list":
        {

            // Récupère les tools dynamiques via votre service proxy
            var tools = await proxySvc.GetToolsAsync(openapiUrl, baseUrl);

            var toolsArr = new JsonArray();
            foreach (var t in tools)
            {
                toolsArr.Add(new JsonObject
                {
                    ["name"]        = t.Name,
                    ["title"]       = t.Description,
                    ["description"] = t.Description,
                    ["inputSchema"] = JsonNode.Parse(JsonSerializer.Serialize(t.InputSchema))
                });
            }

            resp["result"] = new JsonObject { ["tools"] = toolsArr };
            break;
        }

        /* ---------------------------------------------------------------
         * 3. tools/call
         *    params: { name: "...", arguments: { ... } , url, base_url }
         * ------------------------------------------------------------- */
        case "tools/call":
        {
            if (!root.TryGetProperty("params", out var p))
            {
                resp["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Missing params" };
                break;
            }
            string toolName = p.GetProperty("name").GetString() ?? string.Empty;
            var    args     = p.GetProperty("arguments");

            /* ---- tool dynamique proxifié --------------------------- */
            var dynResult = await proxySvc.ExecuteToolAsync(
                                openapiUrl,
                                toolName,
                                JsonSerializer.Deserialize<object>(args.GetRawText())!,
                                baseUrl);
            resp["result"] = new JsonObject
            {
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = JsonNode.Parse(JsonSerializer.Serialize(dynResult))
                    }
                }
            };
            break;
        }

        /* -----------------------------------------------------------
         * méthode inconnue
         * --------------------------------------------------------- */
        default:
            resp["error"] = new JsonObject
            {
                ["code"]    = -32601,
                ["message"] = "Method not found"
            };
            break;
    }

    return Results.Json(resp);
});

app.MapControllers();



app.Run();
