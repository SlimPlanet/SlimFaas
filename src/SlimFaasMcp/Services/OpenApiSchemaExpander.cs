using System.Text.Json;

namespace SlimFaasMcp.Services;

public class OpenApiSchemaExpander
{
    private readonly JsonElement _root;
    private readonly Dictionary<string, object> _refCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);
    private readonly int _maxDepth;

    public OpenApiSchemaExpander(JsonElement root, int maxDepth = 64)
    {
        _root = root;
        _maxDepth = Math.Max(8, maxDepth);
    }

    private static string? AsString(JsonElement e)
    {
        return e.ValueKind switch
        {
            JsonValueKind.String => e.GetString(),
            JsonValueKind.Number => e.ToString(),
            JsonValueKind.True or JsonValueKind.False => e.GetBoolean().ToString(),
            _ => null
        };
    }

    private static string? ReadType(JsonElement schema)
    {
        if (!schema.TryGetProperty("type", out var t)) return null;
        return t.ValueKind switch
        {
            JsonValueKind.String => t.GetString(),
            JsonValueKind.Array  => t.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : null)
                .FirstOrDefault(s => !string.IsNullOrEmpty(s)),
            _ => null
        };
    }

    private static string? ReadStringProp(JsonElement obj, string propName)
    {
        if (!obj.TryGetProperty(propName, out var e)) return null;
        return AsString(e);
    }


    public object ExpandSchema(JsonElement schema) => ExpandSchema(schema, 0);

    private object ExpandSchema(JsonElement schema, int depth)
    {
        if (depth > _maxDepth)
            return new Dictionary<string, object> { ["$ref"] = "#", ["truncated"] = true };

        // ----- $ref ------------------------------------------------------
        if (schema.TryGetProperty("$ref", out var refProp))
        {
            var refPath = refProp.GetString() ?? throw new ArgumentException("Invalid $ref: null");

            // Déjà expansé => réutilise la même instance
            if (_refCache.TryGetValue(refPath, out var cached))
                return cached;

            // Si on retombe sur le même $ref pendant l’expansion, retourne le même placeholder (même instance)
            if (_inProgress.Contains(refPath))
            {
                if (_refCache.TryGetValue(refPath, out var ph) && ph is Dictionary<string, object> d)
                {
                    d["x_circular"] = true; // optionnel, pour debug
                    return d;
                }
                // Cas limite (théoriquement jamais atteint)
                var cyclePlaceholder = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["x_ref"] = refPath,
                    ["x_circular"] = true
                };
                _refCache[refPath] = cyclePlaceholder;
                return cyclePlaceholder;
            }

            // Placeholder MCP-safe (pas de "$ref" dans la sortie)
            var placeholder = new Dictionary<string, object>
            {
                ["type"] = "object",
                ["x_ref"] = refPath,
                ["x_circular"] = false
            };
            _refCache[refPath] = placeholder;
            _inProgress.Add(refPath);
            try
            {
                var resolved = ResolveRef(refPath);
                var expanded = ExpandSchema(resolved, depth + 1);

                // Auto-référence pure : laisse le placeholder tel quel
                if (ReferenceEquals(expanded, placeholder))
                    return placeholder;

                if (expanded is Dictionary<string, object> dict)
                {
                    foreach (var kv in dict)
                        placeholder[kv.Key] = kv.Value;

                    // Optionnel : nettoie les métadonnées pour une sortie plus "propre"
                    placeholder.Remove("x_circular");
                    placeholder.Remove("x_ref");
                    return placeholder; // même instance réutilisée partout
                }

                // Expansion non-dictionnaire (enum/primitive/etc.) : remplace dans le cache
                _refCache[refPath] = expanded;
                return expanded;
            }
            finally
            {
                _inProgress.Remove(refPath);
            }
        }

        // ----- Combinators (anyOf/oneOf/allOf) --------------------------
        if (schema.TryGetProperty("anyOf", out var anyOfArr) && anyOfArr.ValueKind == JsonValueKind.Array)
            return ExpandComposite(schema, "anyOf", anyOfArr, depth);

        if (schema.TryGetProperty("oneOf", out var oneOfArr) && oneOfArr.ValueKind == JsonValueKind.Array)
            return ExpandComposite(schema, "oneOf", oneOfArr, depth);

        if (schema.TryGetProperty("allOf", out var allOfArr) && allOfArr.ValueKind == JsonValueKind.Array)
            return ExpandComposite(schema, "allOf", allOfArr, depth);

        // ----- Enum ------------------------------------------------------
        if (schema.TryGetProperty("enum", out var enumProp))
        {
            var values = enumProp.EnumerateArray()
                .Select(AsString)
                .Where(v => !string.IsNullOrEmpty(v))
                .ToArray();

            var dict = new Dictionary<string, object>
            {
                ["type"] = ReadType(schema) ?? "string",
                ["enum"] = values
            };
            var d = ReadStringProp(schema, "description");
            if (!string.IsNullOrWhiteSpace(d))
                dict["description"] = d!;
            var t = ReadStringProp(schema, "title");
            if (!string.IsNullOrWhiteSpace(t))
                dict["title"] = t!;
            return dict;
        }

        // ----- Type heuristics ------------------------------------------
        var type = ReadType(schema);

        // Objet implicite: pas de type mais des properties => traiter comme object
        if ((type is null || type == "object") && schema.TryGetProperty("properties", out var propsObj))
        {
            var dict = new Dictionary<string, object> { ["type"] = "object" };
            var properties = new Dictionary<string, object>();
            foreach (var prop in propsObj.EnumerateObject())
                properties[prop.Name] = ExpandSchema(prop.Value, depth + 1);
            dict["properties"] = properties;

            if (schema.TryGetProperty("required", out var reqArr) && reqArr.ValueKind == JsonValueKind.Array)
                dict["required"] = reqArr.EnumerateArray().Select(AsString).Where(s => !string.IsNullOrEmpty(s)).ToArray();

            if (schema.TryGetProperty("additionalProperties", out var addProps))
            {
                dict["additionalProperties"] =
                    addProps.ValueKind == JsonValueKind.Object
                        ? ExpandSchema(addProps, depth + 1)
                        : addProps.ValueKind == JsonValueKind.True
                            ? true
                            : addProps.ValueKind == JsonValueKind.False
                                ? false
                                : (object)true; // fallback permissif
            }

            var d = ReadStringProp(schema, "description");
            if (!string.IsNullOrWhiteSpace(d))
               dict["description"] = d!;
            var t = ReadStringProp(schema, "title");
            if (!string.IsNullOrWhiteSpace(t))
               dict["title"] = t!;
            return dict;
        }

        // ----- Array -----------------------------------------------------
        if (type == "array" && schema.TryGetProperty("items", out var items))
        {
            var dict = new Dictionary<string, object>
            {
                ["type"]  = "array",
                ["items"] = ExpandSchema(items, depth + 1)
            };
            {
                var d1 = ReadStringProp(schema, "description");
                if (!string.IsNullOrWhiteSpace(d1))
                    dict["description"] = d1!;
                var t1 = ReadStringProp(schema, "title");
                if (!string.IsNullOrWhiteSpace(t1))
                    dict["title"] = t1!;
            }
            return dict;
        }

        // ----- Primitive + contraintes ----------------------------------
        var resultDict = new Dictionary<string, object>();
        if (type != null) resultDict["type"] = type;
        var f2 = ReadStringProp(schema, "format");
        if (!string.IsNullOrWhiteSpace(f2))
            resultDict["format"] = f2!;
        var d2 = ReadStringProp(schema, "description");
        if (!string.IsNullOrWhiteSpace(d2))
            resultDict["description"] = d2!;
        var t2 = ReadStringProp(schema, "title");
        if (!string.IsNullOrWhiteSpace(t2))
            resultDict["title"] = t2!;

        foreach (var prop in schema.EnumerateObject())
        {
            if (resultDict.ContainsKey(prop.Name)) continue;

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                    resultDict[prop.Name] = AsString(prop.Value) ?? "No value provided";
                    break;
                case JsonValueKind.Number:
                    if (prop.Value.TryGetInt32(out var i)) resultDict[prop.Name] = i;
                    else if (prop.Value.TryGetInt64(out var l)) resultDict[prop.Name] = l;
                    else resultDict[prop.Name] = prop.Value.GetDouble();
                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    resultDict[prop.Name] = prop.Value.GetBoolean();
                    break;
                case JsonValueKind.Array:
                    if (prop.NameEquals("anyOf") || prop.NameEquals("oneOf") || prop.NameEquals("allOf"))
                    {
                        var list = new List<object>();
                        foreach (var sub in prop.Value.EnumerateArray())
                            list.Add(ExpandSchema(sub, depth + 1));
                        resultDict[prop.Name] = list;
                    }
                    else
                    {
                        resultDict[prop.Name] = prop.Value
                            .EnumerateArray()
                            .Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : v.ToString())
                            .ToArray();
                    }
                    break;
                case JsonValueKind.Object:
                    // Cas notables: items/object sans type, additionalProperties object, etc.
                    // On essaie une expansion prudente (non récursive massive) :
                    resultDict[prop.Name] = ExpandSchema(prop.Value, depth + 1);
                    break;
            }
        }

        // Si vraiment vide, renvoyer un stub générique
        return resultDict.Count > 0 ? resultDict : new Dictionary<string, object> { ["type"] = type ?? "object" };
    }

    private object ExpandComposite(JsonElement schema, string keyword, JsonElement arr, int depth)
    {
        var list = new List<object>();
        foreach (var item in arr.EnumerateArray())
            list.Add(ExpandSchema(item, depth + 1));

        var dict = new Dictionary<string, object> { [keyword] = list };
        var d = ReadStringProp(schema, "description");
        if (!string.IsNullOrWhiteSpace(d))
            dict["description"] = d!;
        var t = ReadStringProp(schema, "title");
        if (!string.IsNullOrWhiteSpace(t))
            dict["title"] = t!;
        return dict;
    }

    private static string UnescapeJsonPointer(string token) =>
        token.Replace("~1", "/").Replace("~0", "~");

    private JsonElement ResolveRef(string refPath)
    {
        if (!refPath.StartsWith("#/", StringComparison.Ordinal))
            throw new ArgumentException($"Only local refs supported, got {refPath}");

        var parts = refPath.Substring(2)
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(UnescapeJsonPointer);

        JsonElement current = _root;
        foreach (var part in parts)
        {
            if (!current.TryGetProperty(part, out var next))
                throw new ArgumentException($"$ref path not found: {refPath} (missing '{part}')");
            current = next;
        }
        return current;
    }
}
