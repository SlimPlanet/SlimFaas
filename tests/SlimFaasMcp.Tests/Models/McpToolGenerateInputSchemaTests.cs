using System.Text.Json.Nodes;
using SlimFaasMcp.Models;
using Xunit;

namespace SlimFaasMcp.Tests.Models;

public class McpToolGenerateInputSchemaTests
{
    [Fact]
    public void GenerateInputSchema_UsesDetailedSchema_WhenSchemaProvided()
    {
        // Arrange: detailed schema (dictionary) for the parameter
        var param = new Parameter
        {
            Name     = "name",
            Required = true,
            Schema   = new Dictionary<string, object?>
            {
                ["type"]        = "string",
                ["description"] = "The name"
            }
        };

        // Act
        JsonNode schema = McpTool.GenerateInputSchema([param]);

        // Assert – root object
        Assert.Equal("object", schema?["type"]!.GetValue<string>());

        // Assert – properties section contains our detailed schema
        var props = schema!["properties"]!.AsObject();
        Assert.True(props.ContainsKey("name"));
        var nameProp = props["name"]!.AsObject();
        Assert.Equal("string", nameProp["type"]!.GetValue<string>());
        Assert.Equal("The name", nameProp["description"]!.GetValue<string>());

        // Assert – required array includes the parameter name
        var required = schema!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Contains("name", required);
    }

    [Fact]
    public void GenerateInputSchema_FallsBackToSimpleSchema_WhenSchemaIsNull()
    {
        // Arrange: no detailed schema, only primitive type & description
        var param = new Parameter
        {
            Name        = "age",
            Description = "Age in years",
            SchemaType  = "integer",
            Required    = false
        };

        // Act
        JsonNode schema = McpTool.GenerateInputSchema([param]);

        var props   = schema!["properties"]!.AsObject();
        var ageProp = props["age"]!.AsObject();

        // Assert – fallback to simple schema
        Assert.Equal("integer", ageProp["type"]!.GetValue<string>());
        Assert.Equal("Age in years", ageProp["description"]!.GetValue<string>());

        // Assert – parameter not required
        var requiredArr = schema!["required"]!.AsArray();
        Assert.DoesNotContain(requiredArr, n => n!.GetValue<string>() == "age");
    }
}
