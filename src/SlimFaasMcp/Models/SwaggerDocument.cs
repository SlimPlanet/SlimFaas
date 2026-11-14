using System.Text.Json.Nodes;

namespace SlimFaasMcp.Models;

public class Endpoint
{
    public string? Name { get; set; }
    public string? Url { get; set; }
    public string? Verb { get; set; }
    public string? Summary { get; set; }
    public List<Parameter>? Parameters { get; set; }

    public string? ContentType { get; set; }

    public JsonNode? ResponseSchema { get; set; }

}

public class Parameter
{
    public string? Name { get; set; }
    public string? In { get; set; } // path, query, body, formData
    public bool Required { get; set; }
    public string? Description { get; set; }
    public string? SchemaType { get; set; }

    public string? Format { get; set; }
    public object? Schema { get; set; }

    // OpenAPI `style` keyword (form, spaceDelimited, pipeDelimited, deepObject, simple), with the OpenAPI default for
    // the parameter location when absent. Swagger 2.0 `collectionFormat` values are mapped to the matching style, and
    // tsv, which OpenAPI 3 dropped, to tabDelimited.
    public string? Style { get; set; }

    // OpenAPI `explode` keyword, with the OpenAPI default when absent: true for the form style, false otherwise.
    public bool Explode { get; set; }

    // True when the parameter is described with `content: application/json` instead of `schema`.
    public bool IsJsonContent { get; set; }
}
