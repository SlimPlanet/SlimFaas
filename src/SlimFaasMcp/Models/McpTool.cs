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

    private static bool IsBinaryArraySchema(object? schemaObj)
    {
        if (schemaObj is not Dictionary<string, object> dict) return false;
        if (!dict.TryGetValue("type", out var t0) || !string.Equals(Convert.ToString(t0), "array", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!dict.TryGetValue("items", out var items) || items is not Dictionary<string, object> idict) return false;
        var itemType  = idict.TryGetValue("type", out var it) ? Convert.ToString(it) : null;
        var itemFmt   = idict.TryGetValue("format", out var f) ? Convert.ToString(f) : null;

        return string.Equals(itemType, "string", StringComparison.OrdinalIgnoreCase)
               && string.Equals(itemFmt,  "binary", StringComparison.OrdinalIgnoreCase);
    }

    private static void EnsureDescription(JsonNode node, string? desc)
    {
        if (string.IsNullOrWhiteSpace(desc)) return;
        if (node is JsonObject obj && !obj.TryGetPropertyValue("description", out _))
            obj["description"] = desc;
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
            // 2.1) Tableau de fichiers (array<binary>) => [{ data, filename?, mimeType? }]
            if (IsBinaryArraySchema(parameter.Schema))
            {
                var itemFileObj = new JsonObject
                {
                    ["type"] = "object",
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
                    ["required"] = new JsonArray("data")
                };

                schemaNode = new JsonObject
                {
                    ["type"]  = "array",
                    ["items"] = itemFileObj
                };
                EnsureDescription(schemaNode, parameter.Description);
            }
            // 2.2) Fichier unique (string + format=binary) => { data, filename?, mimeType? }
            else if (string.Equals(parameter.Format, "binary", StringComparison.OrdinalIgnoreCase))
            {
                schemaNode = new JsonObject
                {
                    ["type"] = "object",
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
                    ["required"] = new JsonArray("data")
                };
                EnsureDescription(schemaNode, parameter.Description);
            }
            // 2.3) Autres champs multipart (string/array/object non binaire) → utiliser le schéma
            else
            {
                schemaNode = parameter.Schema is not null
                    ? SchemaHelpers.ToJsonNode(parameter.Schema)!
                    : new JsonObject { ["type"] = "string" };

                EnsureDescription(schemaNode, parameter.Description);
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

