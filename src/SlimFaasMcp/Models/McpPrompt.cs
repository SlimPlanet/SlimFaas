using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SlimFaasMcp.Models;

public class McpPrompt
{
    public List<string>? ActiveTools { get; set; }
    public List<McpToolOverride>? Tools { get; set; }

    public class McpToolOverride
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
        public JsonNode? InputSchema { get; set; }
    }

    private static JsonSerializerOptions s_options = new()
    {
        PropertyNameCaseInsensitive = true      // ✅ casse indifférente
    };

    public static McpPrompt? ParseMcpPrompt(string? mcpPromptB64)
    {
        if (string.IsNullOrEmpty(mcpPromptB64))
            return null;
        try
        {
            var jsonStr = Encoding.UTF8.GetString(Convert.FromBase64String(mcpPromptB64));

            // Place le resolver généré AOT en tête de chaîne
            s_options.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);

            return JsonSerializer.Deserialize<McpPrompt>(jsonStr, s_options);
        }
        catch
        {
            return null;
        }
    }
}
