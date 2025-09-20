using System.Text.Json;
using System.Text.Json.Nodes;
using SlimFaasMcp.Models;
using Xunit;

namespace SlimFaasMcp.Tests.Models;

public class SchemaHelpersTests
{
    // ---------- Helpers ----------
    private static JsonObject AsObj(JsonNode? n) => Assert.IsType<JsonObject>(n);
    private static JsonArray AsArr(JsonNode? n) => Assert.IsType<JsonArray>(n);
    private static JsonValue AsVal(JsonNode? n) => Assert.IsType<JsonValue>(n);

    private static JsonObject Obj(params (string k, object? v)[] kvs)
    {
        var d = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (k, v) in kvs) d[k] = v;
        return AsObj(SchemaHelpers.ToJsonNode(d));
    }

    private static JsonObject ToNode(object? o, int maxDepth = 64) =>
        AsObj(SchemaHelpers.ToJsonNode(o, maxDepth));

    // ---------- Tests ----------

    [Fact]
    public void Null_Returns_NullNode()
    {
        var node = SchemaHelpers.ToJsonNode(null);
        Assert.Null(node);
    }

    [Fact]
    public void Primitives_Are_Converted()
    {
        Assert.Equal("hello", SchemaHelpers.ToJsonNode("hello").GetValue<string>());
        Assert.True(SchemaHelpers.ToJsonNode(true).GetValue<bool>());
        Assert.Equal(42, SchemaHelpers.ToJsonNode(42).GetValue<int>());
        Assert.Equal(42L, SchemaHelpers.ToJsonNode(42L).GetValue<long>());
        Assert.Equal(3.14, SchemaHelpers.ToJsonNode(3.14).GetValue<double>(), 3);
        Assert.Equal(1.23m, SchemaHelpers.ToJsonNode(1.23m).GetValue<decimal>());
    }

    [Fact]
    public void Dictionary_StringObject_To_JsonObject()
    {
        var input = new Dictionary<string, object?>
        {
            ["a"] = "x",
            ["b"] = 1,
            ["c"] = new Dictionary<string, object?> { ["d"] = true }
        };

        var node = ToNode(input);
        Assert.Equal("x", node["a"]!.GetValue<string>());
        Assert.Equal(1, node["b"]!.GetValue<int>());
        Assert.True(AsObj(node["c"]).GetPropertyValue<bool>("d"));
    }

    [Fact]
    public void IList_To_JsonArray()
    {
        var input = new List<object?> { "a", 2, true };
        var arr = AsArr(SchemaHelpers.ToJsonNode(input));
        Assert.Equal("a", arr[0]!.GetValue<string>());
        Assert.Equal(2, arr[1]!.GetValue<int>());
        Assert.True(arr[2]!.GetValue<bool>());
    }

    [Fact]
    public void IEnumerable_NonList_To_JsonArray()
    {
        var set = new HashSet<int> { 1, 2, 3 };
        var arr = AsArr(SchemaHelpers.ToJsonNode(set));
        Assert.Equal(3, arr.Count);
        Assert.True(arr.Select(n => n!.GetValue<int>()).OrderBy(x => x).SequenceEqual(new[] { 1, 2, 3 }));
    }

    [Fact]
    public void JsonElement_Object_And_Array_Are_Converted_Structurally()
    {
        using var doc = JsonDocument.Parse("{\"x\":1,\"y\":[true,\"z\"]}");
        var node = ToNode(doc.RootElement);
        Assert.Equal(1, node["x"]!.GetValue<long>());

        var y = AsArr(node["y"]);
        Assert.True(y[0]!.GetValue<bool>());
        Assert.Equal("z", y[1]!.GetValue<string>());
    }

    [Fact]
    public void JsonElement_Primitives_Are_Converted()
    {
        using var sDoc = JsonDocument.Parse("\"txt\"");
        Assert.Equal("txt", SchemaHelpers.ToJsonNode(sDoc.RootElement).GetValue<string>());

        using var nDoc = JsonDocument.Parse("123");
        Assert.Equal(123, SchemaHelpers.ToJsonNode(nDoc.RootElement).GetValue<long>());

        using var bDoc = JsonDocument.Parse("true");
        Assert.True(SchemaHelpers.ToJsonNode(bDoc.RootElement).GetValue<bool>());
    }

    [Fact]
    public void Cycle_SelfReference_Is_Cut_With_x_cycle()
    {
        var a = new Dictionary<string, object?>();
        a["self"] = a; // self-cycle

        var node = ToNode(a);
        var self = AsObj(node["self"]);
        Assert.True(self["x_cycle"]!.GetValue<bool>());
    }

    [Fact]
    public void Cycle_Mutual_Are_Cut_With_x_cycle()
    {
        var a = new Dictionary<string, object?>();
        var b = new Dictionary<string, object?>();
        a["b"] = b;
        b["a"] = a;

        var nodeA = ToNode(a);
        var nodeB = AsObj(nodeA["b"]);
        var back = AsObj(nodeB["a"]);
        // descending again on the same object should produce an x_cycle marker
        Assert.True(back["x_cycle"]!.GetValue<bool>());
    }

    [Fact]
    public void Cycle_Marker_Preserves_x_ref_When_Present()
    {
        // Simule un placeholder d'expansion avec x_ref
        var a = new Dictionary<string, object?> { ["x_ref"] = "#/components/schemas/Foo" };
        a["self"] = a;

        var node = ToNode(a);
        var self = AsObj(node["self"]);
        Assert.True(self["x_cycle"]!.GetValue<bool>());
        Assert.Equal("#/components/schemas/Foo", self["x_ref"]!.GetValue<string>());
    }

    [Fact]
    public void MaxDepth_Truncates_With_Flag()
    {
        // construit un objet imbriqué profondeur 5
        var deep = new Dictionary<string, object?>();
        var cur = deep;
        for (int i = 0; i < 5; i++)
        {
            var next = new Dictionary<string, object?>();
            cur["n" + i] = next;
            cur = next;
        }

        var node = ToNode(deep, maxDepth: 3); // coupe tôt
        // n0 -> n1 -> n2 -> (truncated)
        var n0 = AsObj(node["n0"]);
        var n1 = AsObj(n0["n1"]);
        var n2 = AsObj(n1["n2"]);
        var trunc = AsObj(n2["n3"]);
        Assert.True(trunc["truncated"]!.GetValue<bool>());
    }

    [Fact]
    public void Unknown_Object_Types_FallBack_To_ToString()
    {
        var weird = new { A = 1, B = 2 }; // anonymous type
        var node = SchemaHelpers.ToJsonNode(weird);
        // Le helper convertit default -> ToString() encapsulé en JsonValue
        Assert.Contains(nameof(weird.A), node!.ToJsonString()); // au minimum, non null
    }

    [Fact]
    public void Mixed_Complex_Structure_Stable()
    {
        var inner = new Dictionary<string, object?>
        {
            ["arr"] = new object?[] { 1, "x", true },
            ["set"] = new HashSet<string> { "a", "b" },
            ["num"] = 9.5
        };
        var outer = new Dictionary<string, object?> { ["inner"] = inner };

        var node = ToNode(outer);
        var innerNode = AsObj(node["inner"]);
        var arr = AsArr(innerNode["arr"]);
        Assert.Equal(3, arr.Count);
        Assert.Equal(1, arr[0]!.GetValue<int>());
        Assert.Equal("x", arr[1]!.GetValue<string>());
        Assert.True(arr[2]!.GetValue<bool>());

        var setArr = AsArr(innerNode["set"]);
        Assert.Equal(2, setArr.Count);
        Assert.True(setArr.Select(n => n!.GetValue<string>()).OrderBy(s => s).SequenceEqual(new[] { "a", "b" }));

        Assert.Equal(9.5, innerNode["num"]!.GetValue<double>(), 3);
    }
}

internal static class JsonObjectExt
{
    public static T GetPropertyValue<T>(this JsonObject o, string name)
    {
        var n = o[name] ?? throw new KeyNotFoundException(name);
        return n.GetValue<T>();
    }
}
