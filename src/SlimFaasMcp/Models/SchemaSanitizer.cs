// SlimFaasMcp/Models/SchemaSanitizer.cs
using System.Collections;
using System.Text.RegularExpressions;

namespace SlimFaasMcp.Models;

public static class SchemaSanitizer
{
    // --- Config clés gardées/supprimées --------------------------------

    private static readonly HashSet<string> AllowedKeys = new(StringComparer.Ordinal)
    {
        // core
        "$schema", "$id", "type", "enum", "const", "default", "description", "title",
        // object
        "properties", "required", "additionalProperties", "minProperties", "maxProperties",
        // array
        "items", "minItems", "maxItems", "uniqueItems",
        // string
        "minLength", "maxLength", "pattern", "format",
        // numeric
        "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "multipleOf",
        // combinators
        "anyOf", "oneOf", "allOf", "not",
        // ⚠️ Ne PAS inclure "components"/"schemas" dans l'output MCP final.
    };

    private static readonly HashSet<string> DropKeys = new(StringComparer.Ordinal)
    {
        "$ref", "nullable", "readOnly", "writeOnly", "deprecated",
        "xml", "discriminator", "externalDocs",
        "example", "examples",
        "requiredIf", "oneOfExclusive",
    };

    private static readonly Regex VendorExt = new(@"^x\-", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Clés dont la valeur est une "map de schémas" : ne pas filtrer les noms enfants
    private static readonly HashSet<string> SchemaMapContainers = new(StringComparer.Ordinal)
    {
        "properties", "patternProperties", "$defs", "definitions"
    };

    /// <summary>
    /// Point d'entrée : sanitize MCP-safe, cycle-safe, depth-safe.
    /// </summary>
    public static object SanitizeForMcp(object node, int maxDepth = 64)
    {
        var visited = new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
        return Sanitize(node, parentKey: null, path: Array.Empty<string>(), visited, depth: 0, maxDepth);
    }

    // --- Implémentation cycle-safe avec mémoïsation ---------------------

    private static object Sanitize(
        object node,
        string? parentKey,
        IReadOnlyList<string> path,
        Dictionary<object, object> visited,
        int depth,
        int maxDepth)
    {
        if (depth > maxDepth)
        {
            // Sortie MCP-safe (compacte) si trop profond
            return new Dictionary<string, object> { ["type"] = "object" };
        }

        switch (node)
        {
            case Dictionary<string, object> dict:
                if (visited.TryGetValue(dict, out var outObj))
                    return outObj; // réutilise la même instance de sortie (évite les cycles)

                // Crée d'abord la sortie et mémorise le mapping source->output
                var newDict = new Dictionary<string, object>(StringComparer.Ordinal);
                visited[dict] = newDict;

                // Sommes-nous dans une "map de schémas" ? (ex : sous "properties")
                var inSchemaMap =
                    (parentKey != null && SchemaMapContainers.Contains(parentKey)) ||
                    // Cas "components.schemas" dans l'input OpenAPI : on ne la veut pas en output MCP,
                    // mais si jamais ça arrive jusqu'ici, on traite "schemas" comme une map de schémas
                    (path.Count >= 2 && path[^2] == "components" && path[^1] == "schemas");

                if (inSchemaMap)
                {
                    // Ne PAS filtrer les noms (ce sont des noms de champs), mais sanitize les valeurs
                    foreach (var (k, v) in dict)
                    {
                        var child = Sanitize(v, parentKey: null, Combine(path, k), visited, depth + 1, maxDepth);
                        newDict[k] = child;
                    }
                    return newDict;
                }

                // Niveau schéma : filtrage sur allow/drop/vendor
                foreach (var (k, v) in dict)
                {
                    if (DropKeys.Contains(k) || VendorExt.IsMatch(k) || !AllowedKeys.Contains(k))
                        continue;

                    newDict[k] = Sanitize(v, parentKey: k, Combine(path, k), visited, depth + 1, maxDepth);
                }

                // Normalisations
                NormalizeAdditionalProperties(newDict);
                NormalizeItems(newDict);
                NormalizeRequired(newDict);

                return newDict;

            case IList list:
                if (visited.TryGetValue(list, out var outList))
                    return outList;

                var newList = new List<object>(list.Count);
                visited[list] = newList;
                foreach (var item in list)
                    newList.Add(Sanitize(item!, parentKey: null, path, visited, depth + 1, maxDepth));
                return newList;

            default:
                return node; // primitives, JsonElement déjà aplati par l’expander, etc.
        }
    }

    // --- Normalisations spécifiques ------------------------------------

    private static void NormalizeAdditionalProperties(Dictionary<string, object> dict)
    {
        if (!dict.TryGetValue("additionalProperties", out var ap)) return;

        if (ap is bool) return;
        if (ap is Dictionary<string, object>) return;

        // Fallback MCP-friendly
        dict["additionalProperties"] = true;
    }

    private static void NormalizeItems(Dictionary<string, object> dict)
    {
        if (!dict.TryGetValue("items", out var items)) return;

        if (items is Dictionary<string, object> || items is IList) return;

        dict["items"] = new Dictionary<string, object> { ["type"] = "object" };
    }

    private static void NormalizeRequired(Dictionary<string, object> dict)
    {
        if (!dict.TryGetValue("required", out var req) || req is not IList reqList) return;
        if (!dict.TryGetValue("properties", out var props) || props is not Dictionary<string, object> propsDict) return;

        var filtered = new List<object>(reqList.Count);
        foreach (var r in reqList)
        {
            if (r is string s && propsDict.ContainsKey(s))
                filtered.Add(s);
        }
        dict["required"] = filtered;
    }

    // --- Utilitaires ----------------------------------------------------

    private static IReadOnlyList<string> Combine(IReadOnlyList<string> prefix, string next)
    {
        var arr = new string[prefix.Count + 1];
        for (int i = 0; i < prefix.Count; i++) arr[i] = prefix[i];
        arr[^1] = next;
        return arr;
    }
}
