
using System.Text.Json;
using System.Text.Json.Nodes;
using YamlDotNet.Serialization;

namespace SlimFaasMcpGateway.Api.Validation;

/// <summary>
/// YAML parser wrapper using YamlDotNet library (AOT-friendly).
/// Provides a consistent API for parsing YAML into a simple node structure.
/// </summary>
public static class SimpleYaml
{
    public abstract record Node;
    public sealed record Scalar(object? Value) : Node;
    public sealed record Mapping(Dictionary<string, Node> Values) : Node;
    public sealed record Sequence(List<Node> Items) : Node;

    public static Node Parse(string yaml)
    {
        if (string.IsNullOrWhiteSpace(yaml))
            return new Mapping(new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase));

        try
        {
            // YamlDotNet: YAML → object → JSON → JsonNode
            var deserializer = new DeserializerBuilder().Build();
            var yamlObject = deserializer.Deserialize(yaml);
            
            if (yamlObject is null)
                return new Mapping(new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase));
            
            // Convert to JSON string then to JsonNode for consistent parsing
            var json = JsonSerializer.Serialize(yamlObject);
            var jsonNode = JsonNode.Parse(json);
            
            return ConvertFromJsonNode(jsonNode);
        }
        catch (Exception ex)
        {
            throw new FormatException($"Invalid YAML: {ex.Message}", ex);
        }
    }

    private static Node ConvertFromJsonNode(JsonNode? node)
    {
        if (node is null)
            return new Scalar(null);

        if (node is JsonObject obj)
        {
            var dict = new Dictionary<string, Node>(StringComparer.OrdinalIgnoreCase);
            foreach (var prop in obj)
            {
                dict[prop.Key] = ConvertFromJsonNode(prop.Value);
            }
            return new Mapping(dict);
        }

        if (node is JsonArray arr)
        {
            var list = new List<Node>();
            foreach (var item in arr)
            {
                list.Add(ConvertFromJsonNode(item));
            }
            return new Sequence(list);
        }

        if (node is JsonValue val)
        {
            return new Scalar(ParseJsonValue(val));
        }

        return new Scalar(node.ToJsonString());
    }

    private static object? ParseJsonValue(JsonValue val)
    {
        if (val.TryGetValue<string>(out var str))
            return str;
        if (val.TryGetValue<bool>(out var b))
            return b;
        if (val.TryGetValue<int>(out var i))
            return i;
        if (val.TryGetValue<long>(out var l))
            return l;
        if (val.TryGetValue<double>(out var d))
            return d;
        if (val.TryGetValue<decimal>(out var dec))
            return dec;
        
        return val.ToJsonString();
    }

    public static Mapping AsMapping(Node node)
        => node as Mapping ?? throw new FormatException("Expected YAML mapping at root.");

    public static Node? TryGet(Mapping map, string key)
        => map.Values.TryGetValue(key, out var n) ? n : null;

    public static string? TryGetString(Mapping map, string key)
        => TryGet(map, key) is Scalar s ? s.Value?.ToString() : null;

    public static bool? TryGetBool(Mapping map, string key)
        => TryGet(map, key) is Scalar s && s.Value is bool b ? b : null;

    public static int? TryGetInt(Mapping map, string key)
    {
        if (TryGet(map, key) is not Scalar s || s.Value is null)
            return null;
        
        return s.Value switch
        {
            int i => i,
            long l => (int)l,
            double d => (int)d,
            string str when int.TryParse(str, out var parsed) => parsed,
            _ => null
        };
    }

    public static Sequence? TryGetSequence(Mapping map, string key)
        => TryGet(map, key) as Sequence;
}
