namespace SlimFaasMcp.Models;

public class McpTool
{
    public string Name { get; set; }
    public string Description { get; set; }
    public object InputSchema { get; set; }
    public EndpointInfo Endpoint { get; set; }

    public class EndpointInfo
    {
        public string Url { get; set; }
        public string Method { get; set; }
    }

    public static object GenerateInputSchema(List<Parameter> parameters)
    {
        var props = parameters.ToDictionary(
            p => p.Name,
            p => new
            {
                type = p.SchemaType ?? "string",
                description = p.Description ?? ""
            }
        );

        var required = parameters.Where(p => p.Required).Select(p => p.Name).ToArray();

        return new
        {
            type = "object",
            properties = props,
            required
        };
    }
}
