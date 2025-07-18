using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlimFaasMcp.Models;

public static class SchemaHelpers
{
    public static JsonNode ToJsonNode(object? obj)
    {
        switch (obj)
        {
            case null:
                return JsonValue.Create((string?)null)!;

            case JsonNode jn:
                return jn;

            case JsonElement je:
                // On garde la représentation brute
                return JsonNode.Parse(je.GetRawText())!;

            case string s:
                return JsonValue.Create(s)!;

            case bool b:
                return JsonValue.Create(b)!;

            case int :
            case long :
            case float :
            case double :
            case decimal :
                return JsonValue.Create(Convert.ToDouble(obj))!;

            case IDictionary<string, object?> dict:
                {
                    var jsonObj = new JsonObject();
                    foreach (var (k, v) in dict)
                        jsonObj[k] = ToJsonNode(v);
                    return jsonObj;
                }

            case IEnumerable<object?> list:
                {
                    var jsonArr = new JsonArray();
                    foreach (var v in list)
                        jsonArr.Add(ToJsonNode(v));
                    return jsonArr;
                }

            default:
                // Fallback : chaîne
                return JsonValue.Create(obj.ToString())!;
        }
    }
}
