using System.Text.Json;

namespace SlimFaasMcp.Services;

public class OpenApiSchemaExpander(JsonElement root, int maxDepth = 64)
{
    private readonly Dictionary<string, object> _refCache = new(StringComparer.Ordinal);
    private readonly HashSet<string> _inProgress = new(StringComparer.Ordinal);
    private readonly int _maxDepth = Math.Max(2, maxDepth);

    private static void CopyIfPresent(JsonElement src, string name, IDictionary<string, object> dst)
    {
        if (!src.TryGetProperty(name, out var v)) return;
        switch (v.ValueKind)
        {
            case JsonValueKind.String:  dst[name] = v.GetString()!; break;
            case JsonValueKind.True:
            case JsonValueKind.False:   dst[name] = v.GetBoolean(); break;
            case JsonValueKind.Number:
                if (v.TryGetInt64(out var l)) dst[name] = l; else dst[name] = v.GetDouble();
                break;
            case JsonValueKind.Array:
                dst[name] = v.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToArray();
                break;
            case JsonValueKind.Object:
                // store raw (avoid re-expanding here)
                dst[name] = v.GetRawText();
                break;
        }
    }

    private static void MergeSiblingMetadata(JsonElement schemaNode, IDictionary<string, object> dst)
    {
        // Priorité : si dst n’a pas la clé, on copie depuis le nœud courant.
        // Useful OAS keys list (adjust as needed)
        foreach (var key in new[]
                 {
                     "description","title","default","deprecated","nullable","example","examples",
                     "format","minimum","maximum","exclusiveMinimum","exclusiveMaximum","pattern",
                     "minLength","maxLength","minItems","maxItems","uniqueItems"
                 })
        {
            if (!dst.ContainsKey(key))
                CopyIfPresent(schemaNode, key, dst);
        }
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

            // If we encounter the same $ref again during expansion, return the same placeholder (same instance)
            if (_inProgress.Contains(refPath))
            {
                if (_refCache.TryGetValue(refPath, out var ph) && ph is Dictionary<string, object> d)
                {
                    d["x_circular"] = true; // optionnel, pour debug
                    return d;
                }
                // Edge case (theoretically never reached)
                var cyclePlaceholder = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["x_ref"] = refPath,
                    ["x_circular"] = true
                };
                _refCache[refPath] = cyclePlaceholder;
                return cyclePlaceholder;
            }

            // MCP-safe placeholder (no "$ref" in the output)
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

                // Pure self-reference: leave the placeholder as is
                if (ReferenceEquals(expanded, placeholder))
                    return placeholder;

                if (expanded is Dictionary<string, object> dict)
                {
                    foreach (var kv in dict)
                        placeholder[kv.Key] = kv.Value;
                    MergeSiblingMetadata(schema, placeholder);
                    // Optional: clean up metadata for a "cleaner" output
                    placeholder.Remove("x_circular");
                    placeholder.Remove("x_ref");
                    return placeholder; // même instance réutilisée partout
                }

                // Non-dictionary expansion (enum/primitive/etc.): replace in the cache
                _refCache[refPath] = expanded;
                return expanded;
            }
            finally
            {
                _inProgress.Remove(refPath);
            }
        }

        // ----- Combinators (anyOf/oneOf/allOf) --------------------------
        {
            var hasCombinator = false;
            var comboDict = new Dictionary<string, object>();

            if (schema.TryGetProperty("anyOf", out var anyOfArr) && anyOfArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<object>();
                foreach (var item in anyOfArr.EnumerateArray())
                    list.Add(ExpandSchema(item, depth + 1));
                comboDict["anyOf"] = list;
                hasCombinator = true;
            }

            if (schema.TryGetProperty("oneOf", out var oneOfArr) && oneOfArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<object>();
                foreach (var item in oneOfArr.EnumerateArray())
                    list.Add(ExpandSchema(item, depth + 1));
                comboDict["oneOf"] = list;
                hasCombinator = true;
            }

            if (schema.TryGetProperty("allOf", out var allOfArr) && allOfArr.ValueKind == JsonValueKind.Array)
            {
                var list = new List<object>();
                foreach (var item in allOfArr.EnumerateArray())
                    list.Add(ExpandSchema(item, depth + 1));
                comboDict["allOf"] = list;
                hasCombinator = true;
            }

            if (hasCombinator)
            {
                var d = ReadStringProp(schema, "description");
                if (!string.IsNullOrWhiteSpace(d)) comboDict["description"] = d!;
                var t = ReadStringProp(schema, "title");
                if (!string.IsNullOrWhiteSpace(t)) comboDict["title"] = t!;
                MergeSiblingMetadata(schema, comboDict);
                return comboDict;
            }
        }

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
            MergeSiblingMetadata(schema, dict);
            return dict;
        }

        // ----- Type heuristics ------------------------------------------
        var type = ReadType(schema);

        // Implicit object: no type but has properties => treat as object
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
                                : (object)true; // permissive fallback
            }

            var d = ReadStringProp(schema, "description");
            if (!string.IsNullOrWhiteSpace(d))
               dict["description"] = d!;
            var t = ReadStringProp(schema, "title");
            if (!string.IsNullOrWhiteSpace(t))
               dict["title"] = t!;
            MergeSiblingMetadata(schema, dict);
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
            MergeSiblingMetadata(schema, dict);
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
                    // Notable cases: items/object without type, additionalProperties object, etc.
                    // We attempt a cautious expansion (not a massive recursive one):
                    resultDict[prop.Name] = ExpandSchema(prop.Value, depth + 1);
                    break;
            }
        }

        // Si vraiment vide, renvoyer un stub générique
        MergeSiblingMetadata(schema, resultDict);
        return resultDict.Count > 0 ? resultDict : new Dictionary<string, object> { ["type"] = type ?? "object" };
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

        JsonElement current = root;
        foreach (var part in parts)
        {
            if (!current.TryGetProperty(part, out var next))
                throw new ArgumentException($"$ref path not found: {refPath} (missing '{part}')");
            current = next;
        }
        return current;
    }
}
