using System.Text.Json;

namespace McpServer.Services;

/// <summary>
/// Registre d’outils exposés par le serveur MCP.
/// </summary>
public class ToolRegistry
{
    private readonly List<Tool> _tools;

    public ToolRegistry()
    {
        /* --- schéma JSON de l’outil “add” ------------------------------ */
        const string addSchemaJson = """
                                     {
                                       "type": "object",
                                       "properties": {
                                         "a": { "type": "number", "description": "First addend"  },
                                         "b": { "type": "number", "description": "Second addend" }
                                       },
                                       "required": ["a", "b"]
                                     }
                                     """;

        var addSchema = JsonSerializer.Deserialize<JsonElement>(addSchemaJson)!;

        _tools = new()
        {
            new Tool(
                Name:        "add",
                Title:       "Addition",
                Description: "Adds two numbers together",
                InputSchema: addSchema)
        };
    }

    /* ------------------------------------------------------------------ */
    public IEnumerable<Tool> ListTools() => _tools;
    public double Add(double a, double b) => a + b;
}
