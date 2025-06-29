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
}
