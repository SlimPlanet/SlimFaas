namespace SlimFaasMcp.Models;

public class Endpoint
{
    public string Name { get; set; }
    public string Url { get; set; }
    public string Verb { get; set; }
    public string Summary { get; set; }
    public List<Parameter> Parameters { get; set; }
}

public class Parameter
{
    public string Name { get; set; }
    public string In { get; set; } // path, query, body
    public bool Required { get; set; }
    public string Description { get; set; }
    public string SchemaType { get; set; }
    public string? Schema { get; set; }
}
