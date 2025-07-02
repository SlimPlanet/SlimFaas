using System.Text.Json;

public record Tool(string Name, string Title, string Description, JsonElement InputSchema);
