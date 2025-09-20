// Tests/Services/OpenApiSchemaExpanderTests.cs
using System.Text.Json;
using SlimFaasMcp.Services;
using Xunit;

public class OpenApiSchemaExpanderTests
{
    private const string SwaggerSkeleton = """
                                           {
                                             "components": {
                                               "schemas": {
                                                 "Pet": {
                                                   "type": "object",
                                                   "description": "A pet",
                                                   "properties": {
                                                     "id":   { "type": "integer", "format": "int64" },
                                                     "name": { "type": "string"  }
                                                   },
                                                   "required": ["id","name"]
                                                 }
                                               }
                                             }
                                           }
                                           """;

    [Fact]
    public void ExpandSchema_Should_ResolveLocalRef()
    {
        using var doc = JsonDocument.Parse(SwaggerSkeleton);
        var expander = new OpenApiSchemaExpander(doc.RootElement);

        var refSchema = JsonDocument.Parse("""{ "$ref":"#/components/schemas/Pet" }""").RootElement;
        var expanded  = expander.ExpandSchema(refSchema) as IDictionary<string, object>;

        Assert.NotNull(expanded);
        Assert.Equal("object", expanded!["type"]);
        var props = (IDictionary<string, object>)expanded["properties"];
        Assert.True(props.ContainsKey("id"));
        Assert.True(props.ContainsKey("name"));
        var required = (string[])expanded["required"];
        Assert.Equal(new[] { "id", "name" }, required);
    }

       private readonly JsonDocument _doc;
    private readonly OpenApiSchemaExpander _expander;


    public OpenApiSchemaExpanderTests()
    {
        const string openApi = """
        {
          "openapi": "3.1.0",
          "components": {
            "schemas": {
              "Pet": {
                "type": "object",
                "properties": {
                  "id":   { "type": "integer", "format": "int64" },
                  "name": { "type": "string" },
                  "tag":  { "type": "string" }
                },
                "required": ["id", "name"]
              },
              "Pets": {
                "type": "array",
                "items": { "$ref": "#/components/schemas/Pet" }
              },
              "PetStatus": {
                "type": "string",
                "enum": ["available", "pending", "sold"],
                "description": "pet status"
              },
              "Price": {
                "type": "integer",
                "minimum": 0,
                "maximum": 1000,
                "description": "price"
              }
            }
          }
        }
        """;

        _doc = JsonDocument.Parse(openApi);
        _expander = new OpenApiSchemaExpander(_doc.RootElement);
    }

    private JsonElement Schema(string name) => _doc.RootElement
        .GetProperty("components").GetProperty("schemas").GetProperty(name);

    [Fact]
    public void ExpandSchema_ResolvesLocalRef()
    {
        var arraySchemaElement = Schema("Pets");

        var resultObj = Assert.IsType<Dictionary<string, object>>(_expander.ExpandSchema(arraySchemaElement));
        Assert.Equal("array", resultObj["type"]);

        // "items" should now be a fully expanded Pet schema (object with properties)
        var items = Assert.IsType<Dictionary<string, object>>(resultObj["items"]);
        Assert.Equal("object", items["type"]);

        var properties = Assert.IsType<Dictionary<string, object>>(items["properties"]);
        Assert.Contains("id", properties.Keys);
        Assert.Contains("name", properties.Keys);

        var required = Assert.IsType<string[]>(items["required"]);
        Assert.Contains("id", required);
        Assert.Contains("name", required);

        // Second call should reuse the resolved Pet schema cached in _refCache
        var second      = _expander.ExpandSchema(arraySchemaElement);
        var firstItems  = (Dictionary<string, object>)resultObj["items"];
        var secondItems = (Dictionary<string, object>)((Dictionary<string, object>)second)["items"];
        Assert.Same(firstItems, secondItems);
    }

    [Fact]
    public void ExpandSchema_HandlesEnum()
    {
        var enumSchemaElement = Schema("PetStatus");

        var result = Assert.IsType<Dictionary<string, object>>(_expander.ExpandSchema(enumSchemaElement));
        Assert.Equal("string", result["type"]);
        var enumValues = Assert.IsType<string[]>(result["enum"]);
        Assert.Equal(new[] { "available", "pending", "sold" }, enumValues);
        Assert.Equal("pet status", result["description"]);
    }

    [Fact]
    public void ExpandSchema_HandlesPrimitiveConstraints()
    {
        var priceSchema = Schema("Price");
        var dict = Assert.IsType<Dictionary<string, object>>(_expander.ExpandSchema(priceSchema));

        Assert.Equal("integer", dict["type"]);
        Assert.Equal(0, dict["minimum"]);
        Assert.Equal(1000, dict["maximum"]);
    }

    [Fact]
    public void ExpandSchema_CyclicRefs_ReusesSameInstances_NoExplosion()
    {
        const string cyc = """
                           {
                             "openapi": "3.1.0",
                             "components": {
                               "schemas": {
                                 "A": {
                                   "type": "object",
                                   "properties": {
                                     "b": { "$ref": "#/components/schemas/B" }
                                   }
                                 },
                                 "B": {
                                   "type": "object",
                                   "properties": {
                                     "a": { "$ref": "#/components/schemas/A" }
                                   }
                                 }
                               }
                             }
                           }
                           """;

        using var doc = JsonDocument.Parse(cyc);
        var expander = new OpenApiSchemaExpander(doc.RootElement, maxDepth: 64);

        // On part d'A (top-level)
        var aSchema = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("A");
        var expandedA = Assert.IsType<Dictionary<string, object>>(expander.ExpandSchema(aSchema));

        // A.properties.b => placeholder de B (rempli)
        var propsA = Assert.IsType<Dictionary<string, object>>(expandedA["properties"]);
        var bFromA = Assert.IsType<Dictionary<string, object>>(propsA["b"]);

        // B.properties.a => placeholder de A (rempli)
        var propsB = Assert.IsType<Dictionary<string, object>>(bFromA["properties"]);
        var aFromB = Assert.IsType<Dictionary<string, object>>(propsB["a"]);

        // On ne peut pas garantir que 'expandedA' === placeholder d'A,
        // mais on peut garantir la boucle sur le *placeholder* de B :
        // aFromB.properties.b doit être EXACTEMENT le même objet que bFromA.
        var propsA2 = Assert.IsType<Dictionary<string, object>>(aFromB["properties"]);
        var bFromA2 = Assert.IsType<Dictionary<string, object>>(propsA2["b"]);
        Assert.Same(bFromA, bFromA2);

        // Et symétriquement, la boucle sur le *placeholder* d'A :
        var propsB2 = Assert.IsType<Dictionary<string, object>>(bFromA2["properties"]);
        var aFromB2 = Assert.IsType<Dictionary<string, object>>(propsB2["a"]);
        Assert.Same(aFromB, aFromB2);
    }


    [Fact]
    public void ExpandSchema_SelfRef_ReusesSameInstance()
    {
        const string self = """
                            {
                              "openapi": "3.1.0",
                              "components": {
                                "schemas": {
                                  "Node": {
                                    "type": "object",
                                    "properties": {
                                      "self": { "$ref": "#/components/schemas/Node" }
                                    }
                                  }
                                }
                              }
                            }
                            """;
        using var doc = JsonDocument.Parse(self);
        var expander = new OpenApiSchemaExpander(doc.RootElement, maxDepth: 64);

        var nodeEl = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("Node");
        var expandedNode = Assert.IsType<Dictionary<string, object>>(expander.ExpandSchema(nodeEl));
        var props = Assert.IsType<Dictionary<string, object>>(expandedNode["properties"]);

        // 'self' is the placeholder for Node (filled), distinct from the top-level
        var selfRef = Assert.IsType<Dictionary<string, object>>(props["self"]);
        Assert.NotSame(expandedNode, selfRef);

        // The self-reference correctly loops back to THE SAME instance (placeholder reused)
        var props2 = Assert.IsType<Dictionary<string, object>>(selfRef["properties"]);
        var selfRef2 = Assert.IsType<Dictionary<string, object>>(props2["self"]);
        Assert.Same(selfRef, selfRef2);
    }


    [Fact]
    public void ExpandSchema_ObjectImplicitType_FromProperties()
    {
        const string implicitObj = """
        {
          "openapi": "3.1.0",
          "components": {
            "schemas": {
              "Implicit": {
                "properties": {
                  "x": { "type": "string" },
                  "y": { "type": "integer" }
                },
                "required": ["x"]
              }
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(implicitObj);
        var expander = new OpenApiSchemaExpander(doc.RootElement);

        var el = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("Implicit");
        var dict = Assert.IsType<Dictionary<string, object>>(expander.ExpandSchema(el));
        Assert.Equal("object", dict["type"]);
        var props = Assert.IsType<Dictionary<string, object>>(dict["properties"]);
        Assert.Contains("x", props.Keys);
        Assert.Contains("y", props.Keys);
        var req = Assert.IsType<string[]>(dict["required"]);
        Assert.Contains("x", req);
        Assert.DoesNotContain("y", req);
    }

    [Fact]
    public void ExpandSchema_AdditionalProperties_ObjectRef_IsExpanded()
    {
        const string addProps = """
        {
          "openapi": "3.1.0",
          "components": {
            "schemas": {
              "KV": {
                "type": "object",
                "additionalProperties": { "$ref": "#/components/schemas/Value" }
              },
              "Value": {
                "type": "object",
                "properties": { "v": { "type": "string" } }
              }
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(addProps);
        var expander = new OpenApiSchemaExpander(doc.RootElement);

        var kv = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("KV");
        var dict = Assert.IsType<Dictionary<string, object>>(expander.ExpandSchema(kv));
        var ap = dict["additionalProperties"];
        var apDict = Assert.IsType<Dictionary<string, object>>(ap);
        Assert.Equal("object", apDict["type"]);
        var props = Assert.IsType<Dictionary<string, object>>(apDict["properties"]);
        Assert.Contains("v", props.Keys);
    }

    [Fact]
    public void ExpandSchema_Combinators_AreExpanded()
    {
        const string combinators = """
        {
          "openapi": "3.1.0",
          "components": {
            "schemas": {
              "Base": { "type": "string" },
              "Wrap": {
                "anyOf": [
                  { "$ref": "#/components/schemas/Base" },
                  { "type": "number" }
                ],
                "oneOf": [
                  { "type": "integer" }
                ],
                "allOf": [
                  { "type": "object", "properties": { "a": { "type": "boolean" } } }
                ]
              }
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(combinators);
        var expander = new OpenApiSchemaExpander(doc.RootElement);

        var wrap = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("Wrap");
        var dict = Assert.IsType<Dictionary<string, object>>(expander.ExpandSchema(wrap));

        var anyOf = Assert.IsType<List<object>>(dict["anyOf"]);
        var any0 = Assert.IsType<Dictionary<string, object>>(anyOf[0]);
        Assert.Equal("string", any0["type"]); // $ref -> expandi
        var any1 = Assert.IsType<Dictionary<string, object>>(anyOf[1]);
        Assert.Equal("number", any1["type"]);

        var oneOf = Assert.IsType<List<object>>(dict["oneOf"]);
        var o0 = Assert.IsType<Dictionary<string, object>>(oneOf[0]);
        Assert.Equal("integer", o0["type"]);

        var allOf = Assert.IsType<List<object>>(dict["allOf"]);
        var a0 = Assert.IsType<Dictionary<string, object>>(allOf[0]);
        Assert.Equal("object", a0["type"]);
        var props = Assert.IsType<Dictionary<string, object>>(a0["properties"]);
        Assert.Contains("a", props.Keys);
    }

    [Fact]
    public void ExpandSchema_ArrayItemsRef_CacheReuse()
    {
        const string arr = """
        {
          "openapi": "3.1.0",
          "components": {
            "schemas": {
              "Pet": { "type": "object", "properties": { "id": { "type": "integer" } } },
              "Pets": { "type": "array", "items": { "$ref": "#/components/schemas/Pet" } }
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(arr);
        var expander = new OpenApiSchemaExpander(doc.RootElement);

        var pets = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("Pets");
        var r1 = Assert.IsType<Dictionary<string, object>>(expander.ExpandSchema(pets));
        var r2 = Assert.IsType<Dictionary<string, object>>(expander.ExpandSchema(pets));

        var i1 = Assert.IsType<Dictionary<string, object>>(r1["items"]);
        var i2 = Assert.IsType<Dictionary<string, object>>(r2["items"]);
        Assert.Same(i1, i2); // cache _refCache
    }

    [Fact]
    public void ExpandSchema_JsonPointerUnescape_Works_For_TildeAndSlash()
    {
        // Key 'Foo/Bar~Baz' must be referenced via '#/components/schemas/Foo~1Bar~0Baz'
        const string docJson = """
        {
          "openapi": "3.1.0",
          "components": {
            "schemas": {
              "Foo/Bar~Baz": { "type": "string", "description": "ok" },
              "Wrap": { "$ref": "#/components/schemas/Foo~1Bar~0Baz" }
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(docJson);
        var expander = new OpenApiSchemaExpander(doc.RootElement);

        var wrap = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("Wrap");
        var expanded = Assert.IsType<Dictionary<string, object>>(expander.ExpandSchema(wrap));
        Assert.Equal("string", expanded["type"]);
        Assert.Equal("ok", expanded["description"]);
    }

    [Fact]
    public void ExpandSchema_MaxDepth_Truncates_WithMarker()
    {
        // Long chain of refs to trigger truncation
        const string deep = """
        {
          "openapi": "3.1.0",
          "components": {
            "schemas": {
              "S1": { "type": "object", "properties": { "n": { "$ref": "#/components/schemas/S2" } } },
              "S2": { "type": "object", "properties": { "n": { "$ref": "#/components/schemas/S3" } } },
              "S3": { "type": "object", "properties": { "n": { "$ref": "#/components/schemas/S4" } } },
              "S4": { "type": "object", "properties": { "n": { "$ref": "#/components/schemas/S5" } } },
              "S5": { "type": "object", "properties": { "n": { "type": "string" } } }
            }
          }
        }
        """;
        using var doc = JsonDocument.Parse(deep);
        var expander = new OpenApiSchemaExpander(doc.RootElement, maxDepth: 2); // petit pour déclencher la troncature

        var s1 = doc.RootElement.GetProperty("components").GetProperty("schemas").GetProperty("S1");
        var dict = Assert.IsType<Dictionary<string, object>>(expander.ExpandSchema(s1));
        var p1 = Assert.IsType<Dictionary<string, object>>(dict["properties"]);
        var n1 = Assert.IsType<Dictionary<string, object>>(p1["n"]);
        var p2 = Assert.IsType<Dictionary<string, object>>(n1["properties"]);
        var n2 = Assert.IsType<Dictionary<string, object>>(p2["n"]);

        // Au-delà de depth, l’impl renvoie un stub { "$ref": "#", "truncated": true }
        Assert.True(n2.TryGetValue("truncated", out var tr));
        Assert.Equal(true, tr);
    }

    [Fact]
    public void ExpandSchema_InvalidRefPath_ThrowsArgumentException()
    {
        const string invalid = """
        {
          "openapi": "3.1.0",
          "components": { "schemas": { "A": { "type": "string" } } }
        }
        """;
        using var doc = JsonDocument.Parse(invalid);
        var expander = new OpenApiSchemaExpander(doc.RootElement);

        using var refDoc = JsonDocument.Parse("""{ "$ref": "#/components/schemas/DoesNotExist" }""");
        var badRef = refDoc.RootElement;

        var ex = Assert.Throws<ArgumentException>(() => expander.ExpandSchema(badRef));
        Assert.Contains("path not found", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

}
