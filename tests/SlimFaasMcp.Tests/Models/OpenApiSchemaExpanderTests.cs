using System.Text.Json;
using SlimFaasMcp.Services;
using Xunit;

namespace SlimFaasMcp.Tests;

public class OpenApiSchemaExpanderTests
{
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
        Assert.Equal("pet status (available, pending, sold)", result["description"]);
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

}
