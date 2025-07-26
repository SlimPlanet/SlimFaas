using System.Text.Json;

public static class GraphQLQueryBuilder
{
    private static JsonElement Unwrap(JsonElement node)
    {
        while (node.ValueKind == JsonValueKind.Object &&
               node.TryGetProperty("kind", out var k) &&
               (k.GetString() is "NON_NULL" or "LIST") &&
               node.TryGetProperty("ofType", out var inner) &&
               inner.ValueKind == JsonValueKind.Object)
        {
            node = inner;
        }
        return node;
    }

    public static string BuildQuery(string toolName,
                                    IReadOnlyDictionary<string, JsonElement> variables,
                                    JsonDocument schema)
    {
        bool isMutation = toolName.StartsWith("mutation_", StringComparison.Ordinal);
        string opKind   = isMutation ? "mutation" : "query";
        string fieldName = toolName[(opKind.Length + 1)..];

        var root = schema.RootElement.GetProperty("data").GetProperty("__schema");

        // Locate operation type
        if (!root.TryGetProperty(isMutation ? "mutationType" : "queryType", out var opElem) ||
            opElem.ValueKind != JsonValueKind.Object ||
            !opElem.TryGetProperty("name", out var opNameProp))
        {
            return $"{opKind}{{ {fieldName} }}";
        }
        string opTypeName = opNameProp.GetString();
        var opType = root.GetProperty("types").EnumerateArray()
                          .FirstOrDefault(t => t.GetProperty("name").GetString() == opTypeName);
        if (opType.ValueKind == JsonValueKind.Undefined)
            return $"{opKind}{{ {fieldName} }}";

        // Locate field
        var field = opType.GetProperty("fields").EnumerateArray()
                          .FirstOrDefault(f => string.Equals(f.GetProperty("name").GetString(), fieldName, StringComparison.OrdinalIgnoreCase));
        if (field.ValueKind == JsonValueKind.Undefined)
            return $"{opKind}{{ {fieldName} }}";

        // Build variable definitions and argument list
        List<string> defList = new();
        List<string> argList = new();
        if (field.TryGetProperty("args", out var argsArr) && argsArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var arg in argsArr.EnumerateArray())
            {
                if (!arg.TryGetProperty("name", out var argNameProp)) continue;
                string argName = argNameProp.GetString();
                if (string.IsNullOrEmpty(argName)) continue;

                if (!arg.TryGetProperty("type", out var typeProp)) continue;
                var unwrapped = Unwrap(typeProp);
                string gqlType = unwrapped.TryGetProperty("name", out var tName) && tName.GetString() is { Length: >0 } s ? s : "String";
                bool nonNull = typeProp.TryGetProperty("kind", out var kindProp) && kindProp.GetString() == "NON_NULL";

                defList.Add($"${argName}: {gqlType}{(nonNull ? "!" : "")}");
                argList.Add($"{argName}: ${argName}");
            }
        }
        string headerPart = defList.Count > 0 ? $"({string.Join(", ", defList)})" : "";
        string argsPart   = argList.Count > 0 ? $"({string.Join(", ", argList)})" : "";

        // Build selection set
        string selection = "__typename";
        var retNode = Unwrap(field.GetProperty("type"));
        if (retNode.TryGetProperty("name", out var retNameProp) && retNameProp.GetString() is { Length: >0 } retName)
        {
            var retType = root.GetProperty("types").EnumerateArray()
                               .FirstOrDefault(t => t.GetProperty("name").GetString() == retName);
            if (retType.ValueKind != JsonValueKind.Undefined &&
                retType.TryGetProperty("fields", out var fieldsArr) && fieldsArr.ValueKind == JsonValueKind.Array)
            {
                var scalars = fieldsArr.EnumerateArray()
                    .Where(f => {
                        var kind = Unwrap(f.GetProperty("type")).GetProperty("kind").GetString();
                        return kind is "SCALAR" or "ENUM";
                    })
                    .Select(f => f.GetProperty("name").GetString())
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct();
                selection = string.Join(" ", scalars.Any() ? scalars : new[] { "__typename" });
            }
        }

        return $"{opKind}{headerPart}{{ {fieldName}{argsPart}{{ {selection} }} }}";
    }
}
