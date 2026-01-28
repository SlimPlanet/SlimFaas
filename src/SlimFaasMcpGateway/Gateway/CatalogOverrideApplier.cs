using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Gateway;

public enum CatalogKind { Tools, Resources, Prompts }

public interface ICatalogOverrideApplier
{
    byte[] Apply(CatalogKind kind, byte[] upstreamBody, string? overrideYaml);
}

public sealed class CatalogOverrideApplier : ICatalogOverrideApplier
{
    public byte[] Apply(CatalogKind kind, byte[] upstreamBody, string? overrideYaml)
    {
        if (string.IsNullOrWhiteSpace(overrideYaml)) return upstreamBody;

        SimpleYaml.Node rootNode;
        try { rootNode = SimpleYaml.Parse(overrideYaml); }
        catch { return upstreamBody; }

        var root = rootNode as SimpleYaml.Mapping;
        if (root is null) return upstreamBody;

        var sectionKey = kind.ToString().ToLowerInvariant(); // tools/resources/prompts
        if (!root.Values.TryGetValue(sectionKey, out var sectionNode)) return upstreamBody;

        if (sectionNode is not SimpleYaml.Mapping section) return upstreamBody;

        var allow = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (section.Values.TryGetValue("allow", out var allowNode) && allowNode is SimpleYaml.Sequence seq)
        {
            foreach (var it in seq.Items)
                if (it is SimpleYaml.Scalar s && s.Value is not null)
                    allow.Add(s.Value.ToString()!.Trim());
        }

        var overrides = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (section.Values.TryGetValue("overrides", out var overridesNode) && overridesNode is SimpleYaml.Mapping oMap)
        {
            foreach (var toolKv in oMap.Values)
            {
                if (toolKv.Value is SimpleYaml.Mapping props)
                {
                    var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var pkv in props.Values)
                    {
                        if (pkv.Value is SimpleYaml.Scalar ps && ps.Value is not null)
                            dict[pkv.Key] = ps.Value.ToString()!;
                    }
                    overrides[toolKv.Key] = dict;
                }
            }
        }

        try
        {
            var node = JsonNode.Parse(upstreamBody);
            if (node is null) return upstreamBody;

            var (array, wrapKey) = ExtractArray(node, kind);
            if (array is null) return upstreamBody;

            var filtered = new JsonArray();

            foreach (var item in array)
            {
                if (item is not JsonObject obj) { filtered.Add(item); continue; }

                var name = obj["name"]?.GetValue<string>();
                if (allow.Count > 0)
                {
                    if (string.IsNullOrWhiteSpace(name) || !allow.Contains(name))
                        continue;
                }

                if (!string.IsNullOrWhiteSpace(name) && overrides.TryGetValue(name!, out var props))
                {
                    foreach (var pkv in props)
                    {
                        obj[pkv.Key] = pkv.Value;
                    }
                }

                filtered.Add(obj);
            }

            JsonNode outNode;
            if (wrapKey is null)
            {
                outNode = filtered;
            }
            else
            {
                var outObj = node as JsonObject ?? new JsonObject();
                outObj[wrapKey] = filtered;
                outNode = outObj;
            }

            var json = outNode.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            return Encoding.UTF8.GetBytes(json);
        }
        catch
        {
            return upstreamBody;
        }
    }

    private static (JsonArray? Array, string? WrapKey) ExtractArray(JsonNode node, CatalogKind kind)
    {
        if (node is JsonArray arr) return (arr, null);
        if (node is JsonObject obj)
        {
            // common wrappers
            var key = kind switch
            {
                CatalogKind.Tools => "tools",
                CatalogKind.Resources => "resources",
                CatalogKind.Prompts => "prompts",
                _ => null
            };

            if (key is not null && obj[key] is JsonArray arr2) return (arr2, key);
            if (obj["items"] is JsonArray items) return (items, "items");
        }
        return (null, null);
    }
}
