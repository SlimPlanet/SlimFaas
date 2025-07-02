using System.Text.Json;
using System.Text.Json.Nodes;
using McpServer.Services;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ToolRegistry>();   // registre d’outils

var app = builder.Build();


app.MapGet("/mcp", () =>
    Results.StatusCode(StatusCodes.Status405MethodNotAllowed));

/* ---------------------------------------------------------------------------
 *  Endpoint unique JSON-RPC 2.0 : POST /mcp
 * -------------------------------------------------------------------------*/
app.MapPost("/mcp", async (HttpRequest httpRequest, ToolRegistry registry) =>
{
    // 🔐 Extraction du header Authorization
    var authHeader = httpRequest.Headers["Authorization"].FirstOrDefault();
    using var jsonDocument = await JsonDocument.ParseAsync(httpRequest.Body);
    var root = jsonDocument.RootElement;

    /* --- champs JSON-RPC de base ---------------------------------------- */
    string jsonrpc = root.GetProperty("jsonrpc").GetString() ?? "2.0";
    bool hasId    = root.TryGetProperty("id", out var idElem);
    string method = root.GetProperty("method").GetString() ?? string.Empty;

    var response = new JsonObject { ["jsonrpc"] = jsonrpc };
    if (hasId)
    {
        // Convertit le JsonElement (nombre, chaîne, etc.) en JsonNode
        response["id"] = JsonNode.Parse(idElem.GetRawText())!;
    }

    switch (method)
    {
        /* -------- 1. initialize ----------------------------------------- */
        case "initialize":
            response["result"] = new JsonObject
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

        /* -------- 2. tools/list ----------------------------------------- */
        case "tools/list":
            var toolsArr = new JsonArray();
            foreach (var t in registry.ListTools())
            {
                toolsArr.Add(new JsonObject
                {
                    ["name"]        = t.Name,
                    ["title"]       = t.Title,
                    ["description"] = t.Description,
                    ["inputSchema"] = JsonNode.Parse(t.InputSchema.GetRawText())
                });
            }
            response["result"] = new JsonObject { ["tools"] = toolsArr };
            break;

        /* -------- 3. tools/call ----------------------------------------- */
        case "tools/call":
            if (!root.TryGetProperty("params", out var p))
            {
                response["error"] = new JsonObject { ["code"] = -32602, ["message"] = "Missing params" };
                break;
            }
            string toolName = p.GetProperty("name").GetString() ?? string.Empty;
            var    args     = p.GetProperty("arguments");

            switch (toolName)
            {
                case "add":
                    double a = args.GetProperty("a").GetDouble();
                    double b = args.GetProperty("b").GetDouble();
                    double sum = registry.Add(a, b);

                    response["result"] = new JsonObject
                    {
                        ["content"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["type"] = "text",
                                ["text"] = $"{a} + {b} = {sum}"
                            }
                        }
                    };
                    break;

                default:
                    response["error"] = new JsonObject
                    {
                        ["code"]    = -32602,
                        ["message"] = $"Unknown tool: {toolName}"
                    };
                    break;
            }
            break;

        /* -------- méthode inconnue -------------------------------------- */
        default:
            response["error"] = new JsonObject
            {
                ["code"]    = -32601,
                ["message"] = "Method not found"
            };
            break;
    }

    return Results.Json(response);
});

app.Run();
