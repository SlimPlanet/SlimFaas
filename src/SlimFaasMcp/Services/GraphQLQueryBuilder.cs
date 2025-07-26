using System.Text.Json;

namespace SlimFaasMcp.Services;

/// <summary>
/// Génère dynamiquement la requête GraphQL correspondant au tool.
/// </summary>
public static class GraphQLQueryBuilder
{
    public static string BuildQuery(string toolName,
                                    IReadOnlyDictionary<string, JsonElement> variables,
                                    JsonDocument schema)
    {
        bool isMutation = toolName.StartsWith("mutation_");
        string opKind   = isMutation ? "mutation" : "query";
        string fieldName = toolName[(opKind.Length + 1)..];

        var root = schema.RootElement.GetProperty("data").GetProperty("__schema");
        var opTypeElem  = root.GetProperty(isMutation ? "mutationType" : "queryType");
        var opTypeName  = opTypeElem.ValueKind == JsonValueKind.Null ? null : opTypeElem.GetProperty("name").GetString();
        if (string.IsNullOrEmpty(opTypeName))
            return $"{opKind}{{ {fieldName} }}";

        var opType = root.GetProperty("types").EnumerateArray()
                         .First(t => t.GetProperty("name").GetString() == opTypeName);
        var field  = opType.GetProperty("fields").EnumerateArray()
                         .FirstOrDefault(f => string.Equals(f.GetProperty("name").GetString(), fieldName, StringComparison.OrdinalIgnoreCase));
        if (field.ValueKind == JsonValueKind.Undefined)
            return $"{opKind}{{ {fieldName} }}";

        static JsonElement Unwrap(JsonElement t)
        {
            while (t.ValueKind == JsonValueKind.Object &&
                   t.TryGetProperty("kind", out var k) &&
                   (k.GetString() is "NON_NULL" or "LIST") &&
                   t.TryGetProperty("ofType", out var inner) &&
                   inner.ValueKind == JsonValueKind.Object)
                t = inner;
            return t;
        }

        // --- variables / arguments ---
        var defs = new List<string>();
        var args = new List<string>();
        foreach (var arg in field.GetProperty("args").EnumerateArray())
        {
            var argName = arg.GetProperty("name").GetString()!;
            var unwrapped = Unwrap(arg.GetProperty("type"));
            var gqlType = unwrapped.GetProperty("name").GetString() ?? "String";
            bool nonNull = arg.GetProperty("type").GetProperty("kind").GetString() == "NON_NULL";

            defs.Add($"${argName}: {gqlType}{(nonNull ? "!" : string.Empty)}");
            args.Add($"{argName}: ${argName}");
        }
        var header = defs.Count == 0 ? string.Empty : $"({string.Join(", ", defs)})";
        var argList = args.Count == 0 ? string.Empty : $"({string.Join(", ", args)})";

        // --- selection scalaires ---
        var returnNode = Unwrap(field.GetProperty("type"));
        string selection = "__typename";
        if (returnNode.TryGetProperty("name", out var rn) && rn.GetString() is { Length: >0 } retName)
        {
            var retType = root.GetProperty("types").EnumerateArray()
                              .FirstOrDefault(t => t.GetProperty("name").GetString() == retName);
            if (retType.ValueKind != JsonValueKind.Undefined)
            {
                var scalars = retType.GetProperty("fields").EnumerateArray()
                    .Where(f => {
                        var kind = Unwrap(f.GetProperty("type")).GetProperty("kind").GetString();
                        return kind is "SCALAR" or "ENUM";
                    })
                    .Select(f => f.GetProperty("name").GetString())
                    .ToList();
                if (scalars.Count > 0)
                    selection = string.Join(" ", scalars);
            }
        }

        return $"{opKind}{header}{{ {fieldName}{argList}{{ {selection} }} }}";
    }
}
