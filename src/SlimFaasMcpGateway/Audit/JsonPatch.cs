
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlimFaasMcpGateway.Audit;

/// <summary>
/// A minimal JSON patch mechanism (not RFC6902) designed for:
/// - storing "diffs" between JSON snapshots,
/// - reconstructing snapshots by applying patches in order.
/// Arrays are treated as atomic values (whole array replace).
/// </summary>
public static class JsonPatch
{
    public sealed record Op(string Path, JsonNode? Value, bool Remove);

    public static IReadOnlyList<Op> Create(JsonNode? before, JsonNode? after)
    {
        var ops = new List<Op>();
        DiffInternal(ops, "", before, after);
        return ops;
    }

    public static JsonNode Apply(JsonNode baseNode, IEnumerable<Op> ops)
    {
        foreach (var op in ops)
        {
            ApplyOne(baseNode, op);
        }
        return baseNode;
    }

    public static string Serialize(IReadOnlyList<Op> ops)
        => JsonSerializer.Serialize(ops, AppJsonOptions.Default);

    public static IReadOnlyList<Op> Deserialize(string json)
        => JsonSerializer.Deserialize<List<Op>>(json, AppJsonOptions.Default) ?? new();

    /// <summary>
    /// Creates a text-based unified diff between two JSON nodes using DiffPlex
    /// </summary>
    public static TextDiff.UnifiedDiff CreateTextDiff(JsonNode? before, JsonNode? after)
    {
        var beforeText = before?.ToJsonString(AppJsonOptions.DefaultIndented) ?? "";
        var afterText = after?.ToJsonString(AppJsonOptions.DefaultIndented) ?? "";
        return TextDiff.Create(beforeText, afterText);
    }

    private static void DiffInternal(List<Op> ops, string path, JsonNode? before, JsonNode? after)
    {
        if (JsonEquals(before, after))
            return;

        // null cases
        if (before is null)
        {
            ops.Add(new Op(path, after, Remove: false));
            return;
        }

        if (after is null)
        {
            ops.Add(new Op(path, Value: null, Remove: true));
            return;
        }

        // arrays -> whole replace
        if (before is JsonArray || after is JsonArray)
        {
            ops.Add(new Op(path, after, Remove: false));
            return;
        }

        // objects -> key-level diff
        if (before is JsonObject bObj && after is JsonObject aObj)
        {
            // removed keys
            foreach (var kv in bObj)
            {
                if (!aObj.ContainsKey(kv.Key))
                    ops.Add(new Op(Join(path, kv.Key), Value: null, Remove: true));
            }

            // added/changed keys
            foreach (var kv in aObj)
            {
                bObj.TryGetPropertyValue(kv.Key, out var bVal);
                DiffInternal(ops, Join(path, kv.Key), bVal, kv.Value);
            }

            return;
        }

        // primitive replace
        ops.Add(new Op(path, after, Remove: false));
    }

    private static void ApplyOne(JsonNode root, Op op)
    {
        if (string.IsNullOrEmpty(op.Path))
        {
            // root replace is handled by caller (we keep it simple: replace properties instead)
            if (root is JsonObject rObj && op.Value is JsonObject vObj && !op.Remove)
            {
                rObj.Clear();
                foreach (var kv in vObj) rObj[kv.Key] = kv.Value;
                return;
            }

            throw new InvalidOperationException("Root replace not supported for non-object roots.");
        }

        var (parent, lastSeg) = NavigateToParent(root, op.Path);

        if (parent is JsonObject pobj)
        {
            if (op.Remove) pobj.Remove(lastSeg);
            else pobj[lastSeg] = op.Value?.DeepClone();
            return;
        }

        throw new InvalidOperationException($"Unsupported parent node for path '{op.Path}'.");
    }

    private static (JsonNode Parent, string LastSegment) NavigateToParent(JsonNode root, string path)
    {
        // path format: /a/b/c
        var segs = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segs.Length == 0) throw new InvalidOperationException("Invalid path");

        JsonNode current = root;
        for (var i = 0; i < segs.Length - 1; i++)
        {
            var seg = segs[i];
            if (current is JsonObject obj)
            {
                if (!obj.TryGetPropertyValue(seg, out var next) || next is null)
                {
                    next = new JsonObject();
                    obj[seg] = next;
                }
                current = next;
                continue;
            }

            throw new InvalidOperationException($"Cannot navigate into non-object node at '{seg}'.");
        }

        return (current, segs[^1]);
    }

    private static bool JsonEquals(JsonNode? a, JsonNode? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;

        var aj = a.ToJsonString(AppJsonOptions.Default);
        var bj = b.ToJsonString(AppJsonOptions.Default);
        return string.Equals(aj, bj, StringComparison.Ordinal);
    }

    private static string Join(string path, string seg) => string.IsNullOrEmpty(path) ? "/" + seg : path + "/" + seg;
}

/// <summary>
/// Shared JsonSerializerOptions (AOT friendly).
/// </summary>
public static class AppJsonOptions
{
    public static readonly JsonSerializerOptions Default = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false,
        TypeInfoResolverChain = { SlimFaasMcpGateway.Serialization.ApiJsonContext.Default }
    };

    public static readonly JsonSerializerOptions DefaultIndented = new(System.Text.Json.JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        TypeInfoResolverChain = { SlimFaasMcpGateway.Serialization.ApiJsonContext.Default }
    };
}
