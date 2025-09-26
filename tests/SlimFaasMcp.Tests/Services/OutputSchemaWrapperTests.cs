using System.Text.Json.Nodes;
using SlimFaasMcp.Services;
using Xunit;

namespace SlimFaasMcp.Tests;

public class OutputSchemaWrapperTests
{
    [Fact]
    public void Wrap_ScalarType_ShouldWrapIntoValueObject()
    {
        // Arrange
        var original = new JsonObject
        {
            ["type"] = "string",
            ["description"] = "A scalar string",
            ["format"] = "uuid"
        };

        // Act
        var wrapped = OutputSchemaWrapper.WrapForStructuredContent(original) as JsonObject;

        // Assert
        Assert.NotNull(wrapped);
        Assert.Equal("object", wrapped!["type"]!.GetValue<string>());

        var props = wrapped!["properties"]!.AsObject();
        Assert.True(props.ContainsKey("value"));
        var valueSchema = props["value"]!.AsObject();

        Assert.Equal("string", valueSchema["type"]!.GetValue<string>());
        Assert.Equal("uuid", valueSchema["format"]!.GetValue<string>());

        var required = wrapped!["required"]!.AsArray();
        Assert.Contains("value", required.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Wrap_ArrayType_ShouldWrapIntoItemsObject()
    {
        // Arrange
        var original = new JsonObject
        {
            ["type"] = "array",
            ["items"] = new JsonObject
            {
                ["type"] = "integer",
                ["format"] = "int32"
            }
        };

        // Act
        var wrapped = OutputSchemaWrapper.WrapForStructuredContent(original) as JsonObject;

        // Assert
        Assert.NotNull(wrapped);
        Assert.Equal("object", wrapped!["type"]!.GetValue<string>());

        var props = wrapped!["properties"]!.AsObject();
        Assert.True(props.ContainsKey("items"));

        // On vérifie que le schéma array d'origine est préservé sous "items"
        var itemsSchema = props["items"]!.AsObject();
        Assert.Equal("array", itemsSchema["type"]!.GetValue<string>());

        var innerItems = itemsSchema["items"]!.AsObject();
        Assert.Equal("integer", innerItems["type"]!.GetValue<string>());
        Assert.Equal("int32", innerItems["format"]!.GetValue<string>());

        var required = wrapped!["required"]!.AsArray();
        Assert.Contains("items", required.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Wrap_ObjectType_ShouldRemainUnchanged()
    {
        // Arrange
        var original = new JsonObject
        {
            ["type"] = "object",
            ["title"] = "Person",
            ["properties"] = new JsonObject
            {
                ["name"] = new JsonObject { ["type"] = "string" },
                ["age"]  = new JsonObject { ["type"] = "integer", ["minimum"] = 0 }
            },
            ["required"] = new JsonArray("name")
        };

        // Act
        var wrapped = OutputSchemaWrapper.WrapForStructuredContent(original);

        // Assert (comparaison structurelle via sérialisation)
        Assert.Equal(original.ToJsonString(), wrapped.ToJsonString());
    }

    [Fact]
    public void Wrap_NoType_ShouldDefaultEmptyJson()
    {
        // Arrange
        var original = new JsonObject
        {
            ["description"] = "No explicit type",
            ["minimum"] = 0
        };

        // Act
        var wrapped = OutputSchemaWrapper.WrapForStructuredContent(original) as JsonObject;

        // Assert
        Assert.NotNull(wrapped);
        Assert.Equal("{}", wrapped.ToJsonString());
    }

    [Fact]
    public void Wrap_NullInput_ShouldDefaultEmptyJson()
    {
        // Arrange
        JsonNode? original = null;

        // Act
        var wrapped = OutputSchemaWrapper.WrapForStructuredContent(original) as JsonObject;

        // Assert
        Assert.NotNull(wrapped);
        Assert.Equal("{}", wrapped.ToJsonString());
    }

    [Theory]
    [InlineData("number")]
    [InlineData("integer")]
    [InlineData("boolean")]
    [InlineData("null")]
    public void Wrap_OtherScalarTypes_ShouldWrapIntoValueObject(string scalarType)
    {
        // Arrange
        var original = new JsonObject
        {
            ["type"] = scalarType,
            ["description"] = "scalar"
        };

        // Act
        var wrapped = OutputSchemaWrapper.WrapForStructuredContent(original) as JsonObject;

        // Assert
        Assert.NotNull(wrapped);
        Assert.Equal("object", wrapped!["type"]!.GetValue<string>());

        var props = wrapped!["properties"]!.AsObject();
        Assert.True(props.ContainsKey("value"));

        var valueSchema = props["value"]!.AsObject();
        Assert.Equal(scalarType, valueSchema["type"]!.GetValue<string>());

        var required = wrapped!["required"]!.AsArray();
        Assert.Contains("value", required.Select(n => n!.GetValue<string>()));
    }

    [Fact]
    public void Wrap_ArraySchema_ShouldKeepMetadataOnArray()
    {
        // Arrange
        var original = new JsonObject
        {
            ["type"] = "array",
            ["description"] = "list of things",
            ["minItems"] = 1,
            ["items"] = new JsonObject { ["type"] = "string" }
        };

        // Act
        var wrapped = OutputSchemaWrapper.WrapForStructuredContent(original) as JsonObject;

        // Assert
        var props = wrapped!["properties"]!.AsObject();
        var itemsSchema = props["items"]!.AsObject();

        Assert.Equal("array", itemsSchema["type"]!.GetValue<string>());
        Assert.Equal("list of things", itemsSchema["description"]!.GetValue<string>());
        Assert.Equal(1, itemsSchema["minItems"]!.GetValue<int>());
    }
}
