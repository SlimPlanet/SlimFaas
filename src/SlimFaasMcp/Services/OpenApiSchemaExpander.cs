using System.Text.Json;

namespace SlimFaasMcp.Services;

public class OpenApiSchemaExpander(JsonElement root)
{
    private readonly Dictionary<string, object> _refCache = new();

    public object ExpandSchema(JsonElement schema)
    {
        // Handle $ref
        if (schema.TryGetProperty("$ref", out var refProp))
        {
            var refPath = refProp.GetString();
            if (refPath == null)
                throw new ArgumentException("Invalid $ref, cannot be null");

            if (_refCache.TryGetValue(refPath, out var cached))
                return cached;

            var resolved = ResolveRef(refPath);
            var result = ExpandSchema(resolved);
            _refCache[refPath] = result;
            return result;
        }

        // ---- NEW: handle anyOf/oneOf/allOf BEFORE others -----------------
        if (schema.TryGetProperty("anyOf", out var anyOfArr) && anyOfArr.ValueKind == JsonValueKind.Array)
            return ExpandComposite(schema, "anyOf", anyOfArr);

        if (schema.TryGetProperty("oneOf", out var oneOfArr) && oneOfArr.ValueKind == JsonValueKind.Array)
            return ExpandComposite(schema, "oneOf", oneOfArr);

        if (schema.TryGetProperty("allOf", out var allOfArr) && allOfArr.ValueKind == JsonValueKind.Array)
            return ExpandComposite(schema, "allOf", allOfArr);

        var type = schema.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        // Enum
        if (schema.TryGetProperty("enum", out var enumProp))
        {
            var values = enumProp.EnumerateArray()
                .Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() : e.ToString())
                .Where(v => !string.IsNullOrEmpty(v))
                .ToArray();

            var baseDesc = schema.TryGetProperty("description", out var desc)
                ? desc.GetString()
                : null;

            var fullDesc = (baseDesc?.Trim() ?? "")
                           + (values.Length > 0
                               ? (baseDesc is { Length: >0 } ? " " : "")
                                 + $"({string.Join(", ", values)})"
                               : "");

            var dict = new Dictionary<string, object>
            {
                ["type"]        = type ?? "string",
                ["enum"]        = values,
                ["description"] = string.IsNullOrWhiteSpace(fullDesc)
                    ? "No description provided"
                    : fullDesc
            };

            if (schema.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                dict["title"] = title.GetString()!;

            return dict;
        }

        // Object with properties
        if (type == "object")
        {
            var dict = new Dictionary<string, object> { ["type"] = "object" };

            if (schema.TryGetProperty("description", out var desc))
                dict["description"] = desc.GetString() ?? "No description provided";
            if (schema.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                dict["title"] = title.GetString()!;

            if (schema.TryGetProperty("properties", out var props))
            {
                var properties = new Dictionary<string, object>();
                foreach (var prop in props.EnumerateObject())
                    properties[prop.Name] = ExpandSchema(prop.Value);

                dict["properties"] = properties;
            }

            if (schema.TryGetProperty("required", out var reqArr))
                dict["required"] = reqArr.EnumerateArray().Select(x => x.GetString()).ToArray();

            return dict;
        }

        // Array
        if (type == "array" && schema.TryGetProperty("items", out var items))
        {
            var dict = new Dictionary<string, object>
            {
                ["type"]  = "array",
                ["items"] = ExpandSchema(items)
            };
            if (schema.TryGetProperty("description", out var desc))
                dict["description"] = desc.GetString() ?? "No description provided";
            if (schema.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
                dict["title"] = title.GetString()!;
            return dict;
        }

        // Primitive type (+ copy constraints)
        var resultDict = new Dictionary<string, object>();
        if (type != null)
            resultDict["type"] = type;
        if (schema.TryGetProperty("format", out var format))
            resultDict["format"] = format.GetString() ?? "No format provided";
        if (schema.TryGetProperty("description", out var desc2))
            resultDict["description"] = desc2.GetString() ?? "No description provided";
        if (schema.TryGetProperty("title", out var title2) && title2.ValueKind == JsonValueKind.String)
            resultDict["title"] = title2.GetString()!;

        // Other constraints (minimum, maximum, pattern, etc.)
        foreach (var prop in schema.EnumerateObject())
        {
            if (resultDict.ContainsKey(prop.Name))
                continue;

            switch (prop.Value.ValueKind)
            {
                case JsonValueKind.String:
                    resultDict[prop.Name] = prop.Value.GetString() ?? "No value provided";
                    break;

                case JsonValueKind.Number:
                    if (prop.Value.TryGetInt32(out var i))
                        resultDict[prop.Name] = i;
                    else if (prop.Value.TryGetInt64(out var l))
                        resultDict[prop.Name] = l;
                    else
                        resultDict[prop.Name] = prop.Value.GetDouble();
                    break;

                case JsonValueKind.True:
                case JsonValueKind.False:
                    resultDict[prop.Name] = prop.Value.GetBoolean();
                    break;

                case JsonValueKind.Array:
                    // ---- NEW: expand arrays of schemas for combinators, keep others as primitive lists
                    if (prop.NameEquals("anyOf") || prop.NameEquals("oneOf") || prop.NameEquals("allOf"))
                    {
                        var list = new List<object>();
                        foreach (var sub in prop.Value.EnumerateArray())
                            list.Add(ExpandSchema(sub));
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
                    // leave unknown objects as-is or expand shallowly if desired
                    break;
            }
        }

        return resultDict;
    }

    private object ExpandComposite(JsonElement schema, string keyword, JsonElement arr)
    {
        var dict = new Dictionary<string, object> { [keyword] = new List<object>() };
        var list = (List<object>)dict[keyword];

        foreach (var item in arr.EnumerateArray())
            list.Add(ExpandSchema(item)); // <- recurse ($ref handled here)

        if (schema.TryGetProperty("description", out var desc))
            dict["description"] = desc.GetString() ?? "No description provided";
        if (schema.TryGetProperty("title", out var title) && title.ValueKind == JsonValueKind.String)
            dict["title"] = title.GetString()!;

        return dict;
    }

    private JsonElement ResolveRef(string refPath)
    {
        // #/components/schemas/xxx
        if (!refPath.StartsWith("#/"))
            throw new ArgumentException($"Only local refs supported, got {refPath}");
        var path = refPath.Substring(2).Split('/');

        JsonElement current = root;
        foreach (var part in path)
            current = current.GetProperty(part);
        return current;
    }
}
