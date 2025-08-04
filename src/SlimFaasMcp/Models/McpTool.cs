using System.Text.Json.Nodes;

namespace SlimFaasMcp.Models;

public class McpTool
{
    public string? Name { get; set; }
    public string? Description { get; set; }
    public JsonNode InputSchema  { get; set; } = new JsonObject();

    public JsonNode OutputSchema { get; set; } = new JsonObject();
    public EndpointInfo? Endpoint { get; set; }

    public class EndpointInfo
    {
        public string? Url { get; set; }
        public string? Method { get; set; }

        public string? ContentType { get; set; }
    }

    // helper basique (optionnel) pour générer un schéma de sortie « sans introspection »
    public static JsonNode GenerateOutputSchema(object? schema) =>
        SchemaHelpers.ToJsonNode(schema);

    public static JsonNode GenerateInputSchema(List<Parameter> parameters)
    {
        var props = new JsonObject();

        foreach (var parameter in parameters)
        {
            JsonNode schemaNode;

            if (parameter.Schema is not null)               // schéma détaillé (expander)
                schemaNode = SchemaHelpers.ToJsonNode(parameter.Schema);
            else                                    // schéma « simple »
                schemaNode = new JsonObject {
                    ["type"]        = parameter.SchemaType ?? "string",
                    ["description"] = parameter.Description ?? ""
                };

            if (parameter.Name != null)
            {
                props[parameter.Name] = schemaNode;
            }
        }

        var required = parameters.Where(pr => pr.Required)
            .Select(pr => (JsonNode)JsonValue.Create(pr.Name)!)
            .ToArray();

        return new JsonObject {
            ["type"]       = "object",
            ["properties"] = props,
            ["required"]   = new JsonArray(required)
        };
    }

}
