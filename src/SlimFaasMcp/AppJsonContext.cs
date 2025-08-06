using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using SlimFaasMcp.Models;

namespace SlimFaasMcp;

// Contexte JSON minimal qui couvre TOUT ce qu’on manipule dynamiquement.
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Metadata
                     | JsonSourceGenerationMode.Serialization)]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(List<Dictionary<string, JsonElement>>))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonArray))]    // ✅  nouveau
[JsonSerializable(typeof(McpTool))]
[JsonSerializable(typeof(List<McpTool>))]
[JsonSerializable(typeof(McpTool.EndpointInfo))]
[JsonSerializable(typeof(McpPrompt))]
[JsonSerializable(typeof(List<McpPrompt.McpToolOverride>))]
[JsonSerializable(typeof(OAuthProtectedResourceMetadata))]
internal partial class AppJsonContext : JsonSerializerContext { }
