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

    public static JsonNode GenerateInputSchema(List<Parameter> parameters)
    {
        var props = new JsonObject();
        var required = new List<JsonNode>();

        foreach (var parameter in parameters)
        {
            if (string.IsNullOrEmpty(parameter.Name)) continue;

            JsonNode schemaNode;

            // --- MCP: binaire encodé en base64 (contentEncoding) -----------
            if (string.Equals(parameter.In, "formData", StringComparison.OrdinalIgnoreCase))
            {
                if (string.Equals(parameter.Format, "binary", StringComparison.OrdinalIgnoreCase))
                {
                    // Objet { data(base64), filename?, mimeType? }
                    schemaNode = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = parameter.Description ?? "",
                        ["properties"] = new JsonObject
                        {
                            ["data"] = new JsonObject
                            {
                                ["type"] = "string",
                                ["contentEncoding"] = "base64",
                                ["contentMediaType"] = "application/octet-stream"
                            },
                            ["filename"] = new JsonObject { ["type"] = "string" },
                            ["mimeType"] = new JsonObject { ["type"] = "string" }
                        },
                        ["required"] = new JsonArray(JsonValue.Create("data")!)
                    };
                }
                else
                {
                    // Champ texte simple dans le form-data
                    schemaNode = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = parameter.Description ?? ""
                    };
                }
            }
            else if (string.Equals(parameter.In, "body", StringComparison.OrdinalIgnoreCase)
                     && string.Equals(parameter.Format, "binary", StringComparison.OrdinalIgnoreCase))
            {
                // Corps binaire pur (application/octet-stream)
                schemaNode = new JsonObject
                {
                    ["type"] = "object",
                    ["description"] = parameter.Description ?? "Binary body",
                    ["properties"] = new JsonObject
                    {
                        ["data"] = new JsonObject
                        {
                            ["type"] = "string",
                            ["contentEncoding"] = "base64",
                            ["contentMediaType"] = "application/octet-stream"
                        },
                        ["filename"] = new JsonObject { ["type"] = "string" },
                        ["mimeType"] = new JsonObject { ["type"] = "string" }
                    },
                    ["required"] = new JsonArray(JsonValue.Create("data")!)
                };
            }
            else if (parameter.In == "body" && parameter.Schema is not null)
            {
                schemaNode = SchemaHelpers.ToJsonNode(parameter.Schema);
            }
            else if (parameter.Schema is not null)
            {
                schemaNode = SchemaHelpers.ToJsonNode(parameter.Schema);
            }
            else
            {
                schemaNode = new JsonObject {
                    ["type"]        = parameter.SchemaType ?? "string",
                    ["description"] = parameter.Description ?? ""
                };
                if (!string.IsNullOrEmpty(parameter.Format))
                    schemaNode["format"] = parameter.Format;
            }

            props[parameter.Name] = schemaNode;
            if (parameter.Required)
                required.Add(JsonValue.Create(parameter.Name)!);
        }

        return new JsonObject {
            ["type"]       = "object",
            ["properties"] = props,
            ["required"]   = new JsonArray(required.ToArray())
        };
    }

}

