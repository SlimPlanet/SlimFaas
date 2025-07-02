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

        public string ContentType { get; set; }
    }

    public static object GenerateInputSchema(List<Parameter> parameters)
    {
        var props = new Dictionary<string, object>();

        foreach (var p in parameters)
        {
            // Si p.Schema est renseigné (schéma détaillé expandu)
            if (p.Schema != null)
            {
                // Injecte tout le schéma détaillé (déjà bien formatté par ExpandSchema)
                props[p.Name] = p.Schema;
            }
            else
            {
                // Schéma simple (type + description)
                props[p.Name] = new
                {
                    type = p.SchemaType ?? "string",
                    description = p.Description ?? ""
                };
            }
        }

        var required = parameters.Where(p => p.Required).Select(p => p.Name).ToArray();

        return new
        {
            type = "object",
            properties = props,
            required
        };
    }

}
