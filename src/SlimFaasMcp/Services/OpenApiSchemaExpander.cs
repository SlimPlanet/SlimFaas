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
            {
                throw new ArgumentException("Invalid $ref, cannot be null");
            }
            if (_refCache.TryGetValue(refPath, out var cached))
                return cached;

            var resolved = ResolveRef(refPath);
            var result = ExpandSchema(resolved);
            _refCache[refPath] = result; // avoid infinite loop
            return result;
        }

        var type = schema.TryGetProperty("type", out var typeProp) ? typeProp.GetString() : null;

        // Enum
        if (schema.TryGetProperty("enum", out var enumProp))
        {
            return new Dictionary<string, object>
            {
                ["type"] = type,
                ["enum"] = enumProp.EnumerateArray().Select(e => e.GetString()).ToArray(),
                ["description"] = (schema.TryGetProperty("description", out var desc) ? desc.GetString() : null) ?? "No description provided"
            };
        }

        // Object with properties
        if (type == "object")
        {
            var dict = new Dictionary<string, object>
            {
                ["type"] = "object"
            };
            if (schema.TryGetProperty("description", out var desc))
                dict["description"] = desc.GetString() ?? "No description provided";

            if (schema.TryGetProperty("properties", out var props))
            {
                var properties = new Dictionary<string, object>();
                foreach (var prop in props.EnumerateObject())
                {
                    properties[prop.Name] = ExpandSchema(prop.Value);
                }
                dict["properties"] = properties;
            }

            if (schema.TryGetProperty("required", out var reqArr))
            {
                dict["required"] = reqArr.EnumerateArray().Select(x => x.GetString()).ToArray();
            }
            return dict;
        }

        // Array
        if (type == "array" && schema.TryGetProperty("items", out var items))
        {
            var dict = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["items"] = ExpandSchema(items)
            };
            if (schema.TryGetProperty("description", out var desc))
                dict["description"] = desc.GetString() ?? "No description provided";
            return dict;
        }

        // Primitive type
        var resultDict = new Dictionary<string, object>();
        if (type != null)
            resultDict["type"] = type;
        if (schema.TryGetProperty("format", out var format))
            resultDict["format"] = format.GetString() ?? "No format provided";
        if (schema.TryGetProperty("description", out var desc2))
            resultDict["description"] = desc2.GetString() ?? "No description provided";

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
                        resultDict[prop.Name] = i;               // entier
                    else if (prop.Value.TryGetInt64(out var l))
                        resultDict[prop.Name] = l;               // long
                    else
                        resultDict[prop.Name] = prop.Value.GetDouble(); // double / décimal

                    break;
                case JsonValueKind.True:
                case JsonValueKind.False:
                    resultDict[prop.Name] = prop.Value.GetBoolean();
                    break;
                case JsonValueKind.Array:
                    resultDict[prop.Name] = prop.Value.EnumerateArray().Select(v => v.ToString()).ToArray();
                    break;
                case JsonValueKind.Object:
                    // Not expanding unknown objects here, but you could
                    break;
            }
        }

        return resultDict;
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
