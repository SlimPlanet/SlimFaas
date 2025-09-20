using SlimFaasMcp.Models;
using Xunit;

namespace SlimFaasMcp.Tests.Models;

public class SchemaSanitizerTests
{
    // --- Helpers ----------------------------------------------------

    private static Dictionary<string, object> D(params (string k, object v)[] kvs)
        => kvs.ToDictionary(t => t.k, t => t.v, StringComparer.Ordinal);

    private static List<object> L(params object[] items) => new(items);

    private static Dictionary<string, object> Sanitize(object node)
        => (Dictionary<string, object>)SchemaSanitizer.SanitizeForMcp(node);

    private static Dictionary<string, object> AsDict(object? o)
        => Assert.IsType<Dictionary<string, object>>(o!);

    private static List<object> AsList(object? o)
        => Assert.IsType<List<object>>(o!);

    // --- Tests ------------------------------------------------------

    [Fact]
    public void Properties_Map_Preserves_Field_Names_And_Sanitizes_Values()
    {
        var input = D(
            ("type", "object"),
            ("properties", D(
                ("body", D(
                    ("type", "object"),
                    ("properties", D(
                        ("updates", D(
                            ("type", "array"),
                            ("items", D(
                                ("type", "object"),
                                ("properties", D(
                                    ("value", D(("type", "string"))),
                                    // writeOnly doit être supprimé du schéma, pas le champ
                                    ("writeOnlyProp", D(("type", "string"), ("writeOnly", true)))
                                )),
                                ("required", L("value", "missing"))
                            ))
                        ))
                    )),
                    ("required", L("updates"))
                )))
            ));

        var sanitized = Sanitize(input);

        var props = AsDict(sanitized["properties"]);
        Assert.True(props.ContainsKey("body"));

        var body = AsDict(props["body"]);
        var bodyProps = AsDict(body["properties"]);
        Assert.True(bodyProps.ContainsKey("updates"));

        var updates = AsDict(bodyProps["updates"]);
        Assert.Equal("array", updates["type"]);

        var items = AsDict(updates["items"]);
        var itemProps = AsDict(items["properties"]);
        Assert.True(itemProps.ContainsKey("value"));
        Assert.True(itemProps.ContainsKey("writeOnlyProp"));

        var writeOnlyProp = AsDict(itemProps["writeOnlyProp"]);
        Assert.Equal("string", writeOnlyProp["type"]);
        // the writeOnly key has been removed
        Assert.False(writeOnlyProp.ContainsKey("writeOnly"));

        // required filtered to keep only "value"
        var required = AsList(items["required"]);
        Assert.Single(required);
        Assert.Equal("value", required[0]);
    }

    [Fact]
    public void Drop_ref_And_Vendor_Extensions()
    {
        var input = D(
            ("type", "object"),
            ("$ref", "#/components/schemas/Foo"),
            ("x-internal", true),
            ("properties", D(
                ("a", D(("type", "string"), ("x-meta", 1)))
            ))
        );

        var sanitized = Sanitize(input);

        Assert.False(sanitized.ContainsKey("$ref"));
        Assert.False(sanitized.ContainsKey("x-internal"));

        var props = AsDict(sanitized["properties"]);
        var a = AsDict(props["a"]);
        Assert.Equal("string", a["type"]);
        Assert.False(a.ContainsKey("x-meta"));
    }

    [Fact]
    public void Required_Is_Filtered_Against_Existing_Properties()
    {
        var input = D(
            ("type", "object"),
            ("properties", D(
                ("a", D(("type", "string")))
            )),
            ("required", L("a", "b", "c"))
        );

        var sanitized = Sanitize(input);

        var required = AsList(sanitized["required"]);
        Assert.Single(required);
        Assert.Equal("a", required[0]);
    }

    [Theory]
    [InlineData("weird")] // invalid case -> should become true
    [InlineData(123)]
    public void AdditionalProperties_Invalid_Values_Fall_Back_To_True(object invalid)
    {
        var input = D(
            ("type", "object"),
            ("additionalProperties", invalid),
            ("properties", D())
        );

        var sanitized = Sanitize(input);
        Assert.True(sanitized.ContainsKey("additionalProperties"));
        Assert.True((bool)sanitized["additionalProperties"]);
    }

    [Fact]
    public void AdditionalProperties_Bool_And_Dict_Are_Preserved()
    {
        var dictCase = D(
            ("type", "object"),
            ("additionalProperties", D(("type", "string")))
        );
        var boolCase = D(
            ("type", "object"),
            ("additionalProperties", false)
        );

        var s1 = Sanitize(dictCase);
        var s2 = Sanitize(boolCase);

        Assert.IsType<Dictionary<string, object>>(s1["additionalProperties"]);
        Assert.IsType<bool>(s2["additionalProperties"]);
        Assert.False((bool)s2["additionalProperties"]);
    }

    [Fact]
    public void Items_Invalid_Value_Falls_Back_To_Object_Schema()
    {
        var input = D(
            ("type", "array"),
            ("items", "string") // invalid for JSON Schema
        );

        var sanitized = Sanitize(input);
        var items = AsDict(sanitized["items"]);
        Assert.Equal("object", items["type"]);
    }

    [Fact]
    public void Items_Dict_And_Array_Are_Preserved()
    {
        var dictItems = D(("type", "array"), ("items", D(("type", "string"))));
        var listItems = D(("type", "array"), ("items", L(D(("type", "string")), D(("type", "number")))));

        var s1 = Sanitize(dictItems);
        var s2 = Sanitize(listItems);

        Assert.Equal("string", AsDict(s1["items"])["type"]);
        var arr = AsList(s2["items"]);
        Assert.Equal("string", AsDict(arr[0])["type"]);
        Assert.Equal("number", AsDict(arr[1])["type"]);
    }


    [Fact]
    public void Combinators_Are_Kept_And_Sanitized_Recursively()
    {
        var input = D(
            ("anyOf", L(
                D(("type", "string"), ("writeOnly", true)),
                D(("type", "number"), ("x-foo", 1))
            )),
            ("oneOf", L(
                D(("type", "object"), ("properties", D(
                    ("a", D(("type", "string"), ("readOnly", true)))
                )))
            )),
            ("allOf", L(
                D(("type", "object"), ("properties", D(
                    ("b", D(("type", "number")))
                )))
            )),
            ("not", D(("type", "boolean"), ("x-bar", 2)))
        );

        var sanitized = Sanitize(input);

        var anyOf = AsList(sanitized["anyOf"]);
        var any0 = AsDict(anyOf[0]); // writeOnly supprimé
        Assert.Equal("string", any0["type"]);
        Assert.False(any0.ContainsKey("writeOnly"));

        var any1 = AsDict(anyOf[1]); // x-foo supprimé
        Assert.Equal("number", any1["type"]);
        Assert.False(any1.ContainsKey("x-foo"));

        var oneOf = AsList(sanitized["oneOf"]);
        var o0 = AsDict(oneOf[0]);
        var o0props = AsDict(o0["properties"]);
        var a = AsDict(o0props["a"]);
        Assert.Equal("string", a["type"]);
        Assert.False(a.ContainsKey("readOnly"));

        var allOf = AsList(sanitized["allOf"]);
        var a0 = AsDict(allOf[0]);
        var a0props = AsDict(a0["properties"]);
        var b = AsDict(a0props["b"]);
        Assert.Equal("number", b["type"]);

        var not = AsDict(sanitized["not"]);
        Assert.Equal("boolean", not["type"]);
        Assert.False(not.ContainsKey("x-bar"));
    }

    [Fact]
    public void Unknown_TopLevel_Keys_Are_Dropped_But_Descriptions_Are_Kept()
    {
        var input = D(
            ("type", "string"),
            ("description", "hello"),
            ("unknownKey", 12345)
        );

        var sanitized = Sanitize(input);
        Assert.Equal("string", sanitized["type"]);
        Assert.Equal("hello", sanitized["description"]);
        Assert.False(sanitized.ContainsKey("unknownKey"));
    }

    [Fact]
    public void Format_Is_Kept_By_Default()
    {
        var input = D(
            ("type", "integer"),
            ("format", "int64"),
            ("readOnly", true)
        );

        var sanitized = Sanitize(input);
        Assert.Equal("integer", sanitized["type"]);
        Assert.Equal("int64", sanitized["format"]);
        Assert.False(sanitized.ContainsKey("readOnly"));
    }
}
