using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SlimFaasMcp.Models;


[JsonSourceGenerationOptions(
    PropertyNameCaseInsensitive = true,
    GenerationMode = JsonSourceGenerationMode.Metadata | JsonSourceGenerationMode.Serialization
)]
[JsonSerializable(typeof(McpPrompt))]
[JsonSerializable(typeof(List<McpPrompt.McpToolOverride>))]
internal partial class LocalJsonContext : JsonSerializerContext { }


public class McpPrompt
{
    public List<string>? ActiveTools { get; set; }
    public List<McpToolOverride>? Tools { get; set; }

    public class McpToolOverride
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public JsonNode? InputSchema { get; set; }

        public JsonNode? OutputSchema { get; set; }
    }

    public static McpPrompt? ParseMcpPrompt(string? mcpPromptB64)
    {
        if (string.IsNullOrEmpty(mcpPromptB64))
            return null;
        try
        {
            var jsonStr = Encoding.UTF8.GetString(Convert.FromBase64String(mcpPromptB64));
            return JsonSerializer.Deserialize(jsonStr, LocalJsonContext.Default.McpPrompt);
        }
        catch
        {
            return null;
        }
    }
}
