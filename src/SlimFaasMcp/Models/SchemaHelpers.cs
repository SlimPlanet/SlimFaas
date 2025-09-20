using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlimFaasMcp.Models;

public static class SchemaHelpers
{
    // Appelle cette version publique depuis ton code existant.
    public static JsonNode? ToJsonNode(object? value, int maxDepth = 64)
        => ToJsonNodeCore(value, maxDepth, 0, new HashSet<object>(ReferenceEqualityComparer.Instance));

    private static JsonNode? ToJsonNodeCore(object? value, int maxDepth, int depth, HashSet<object> seen)
    {
        if (value is null) return null;

        if (depth > maxDepth)
        {
            var t = new JsonObject { ["truncated"] = true };
            return t;
        }

        switch (value)
        {
            case JsonNode node:
                return node; // already a JsonNode

            case JsonElement elem:
                return FromJsonElement(elem, maxDepth, depth, seen);

            case string s:
                return JsonValue.Create(s);

            case bool b:
                return JsonValue.Create(b);

            case int i:
                return JsonValue.Create(i);

            case long l:
                return JsonValue.Create(l);

            case float f:
                return JsonValue.Create(f);

            case double d:
                return JsonValue.Create(d);

            case decimal m:
                return JsonValue.Create(m);

            case Dictionary<string, object> dict:
            {
                // Détection de cycle par identité
                if (!seen.Add(dict))
                {
                    var cyc = new JsonObject { ["x_cycle"] = true };
                    if (dict.TryGetValue("x_ref", out var r) && r is string rs) cyc["x_ref"] = rs;
                    return cyc;
                }

                var obj = new JsonObject();
                foreach (var kv in dict)
                    obj[kv.Key] = ToJsonNodeCore(kv.Value, maxDepth, depth + 1, seen);

                seen.Remove(dict);
                return obj;
            }

            case IList list:
            {
                if (!seen.Add(list))
                    return new JsonObject { ["x_cycle"] = true };

                var arr = new JsonArray();
                foreach (var item in list)
                    arr.Add(ToJsonNodeCore(item, maxDepth, depth + 1, seen));

                seen.Remove(list);
                return arr;
            }

            case IEnumerable enumerable:
            {
                // Pour IEnumerable non IList (ex: HashSet<object>)
                if (!seen.Add(enumerable))
                    return new JsonObject { ["x_cycle"] = true };

                var arr = new JsonArray();
                foreach (var item in enumerable)
                    arr.Add(ToJsonNodeCore(item, maxDepth, depth + 1, seen));

                seen.Remove(enumerable);
                return arr;
            }

            default:
                // Valeurs arbitraires -> ToString() pour debug
                return JsonValue.Create(value.ToString());
        }
    }

    private static JsonNode? FromJsonElement(JsonElement e, int maxDepth, int depth, HashSet<object> seen)
    {
        // JsonElement n’est pas une référence d’objet managée utile pour un HashSet<object>,
        // on le traite structurellement (pas de cycle via JsonElement).
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var obj = new JsonObject();
                foreach (var prop in e.EnumerateObject())
                    obj[prop.Name] = FromJsonElement(prop.Value, maxDepth, depth + 1, seen);
                return obj;
            }
            case JsonValueKind.Array:
            {
                var arr = new JsonArray();
                foreach (var item in e.EnumerateArray())
                    arr.Add(FromJsonElement(item, maxDepth, depth + 1, seen));
                return arr;
            }
            case JsonValueKind.String:
                return JsonValue.Create(e.GetString());
            case JsonValueKind.Number:
                if (e.TryGetInt64(out var l)) return JsonValue.Create(l);
                if (e.TryGetDouble(out var d)) return JsonValue.Create(d);
                return JsonValue.Create(e.ToString());
            case JsonValueKind.True:
            case JsonValueKind.False:
                return JsonValue.Create(e.GetBoolean());
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return JsonValue.Create(e.ToString());
        }
    }
}
