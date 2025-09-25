using System.Text.Json.Nodes;

namespace SlimFaasMcp.Services;

public static class OutputSchemaWrapper
{
    private static readonly HashSet<string> s_scalarTypes = new(StringComparer.OrdinalIgnoreCase)
        { "string", "number", "integer", "boolean", "null" };

    /// <summary>
    /// Enveloppe un schéma OpenAPI/JSON Schema pour refléter le structuredContent MCP:
    /// - type array   => { type: object, properties: { items: <schemaArray> }, required: ["items"] }
    /// - type scalaire=> { type: object, properties: { value: <schemaScalar> }, required: ["value"] }
    /// - type object  => inchangé (retourne tel quel)
    /// - inconnu      => par défaut scalaire -> wrap "value"
    /// </summary>
    public static JsonNode? WrapForStructuredContent(JsonNode? original)
    {
        original ??= new JsonObject();

        if (original is JsonObject obj && obj.TryGetPropertyValue("type", out var typeNode))
        {
            var typeStr = typeNode?.GetValue<string>()?.Trim();

            if (string.Equals(typeStr, "array", StringComparison.OrdinalIgnoreCase))
            {
                // { type: object, properties: { items: <original> }, required: ["items"] }
                return new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["items"] = obj // on garde le schéma array tel quel ici
                    },
                    ["required"] = new JsonArray("items")
                };
            }

            if (string.Equals(typeStr, "object", StringComparison.OrdinalIgnoreCase))
            {
                // objet tel quel
                return obj;
            }

            if (!string.IsNullOrWhiteSpace(typeStr) && s_scalarTypes.Contains(typeStr!))
            {
                // scalaire -> value
                return new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["value"] = obj
                    },
                    ["required"] = new JsonArray("value")
                };
            }
        }

        // Pas de "type" explicite ou combinators/etc. -> default "scalaire"
        return new JsonObject();
    }
}
