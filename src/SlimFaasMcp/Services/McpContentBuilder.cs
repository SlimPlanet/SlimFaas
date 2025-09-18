using System.Text.Json;
using System.Text.Json.Nodes;
using SlimFaasMcp.Models;

namespace SlimFaasMcp.Services;

public static class McpContentBuilder
{
    /// <summary>
    /// Construit le tableau MCP "content" à partir d'un ProxyCallResult.
    /// - image/*  -> { type:"image",  mimeType, data(base64) }
    /// - audio/*  -> { type:"audio",  mimeType, data(base64) }
    /// - autres binaires -> { type:"resource", resource:{ uri,name,mimeType,size,blob } }
    /// - texte / json -> { type:"text", text }
    /// </summary>
    public static JsonArray Build(ProxyCallResult r)
    {
        var contentArr = new JsonArray();

        if (r.IsBinary && r.Bytes is not null)
        {
            var mime    = string.IsNullOrWhiteSpace(r.MimeType) ? "application/octet-stream" : r.MimeType!;
            var mimeLow = mime.ToLowerInvariant();
            var base64  = Convert.ToBase64String(r.Bytes);

            if (mimeLow.StartsWith("image/"))
            {
                contentArr.Add(new JsonObject {
                    ["type"]     = "image",
                    ["mimeType"] = mime,
                    ["data"]     = base64
                });
            }
            else if (mimeLow.StartsWith("audio/"))
            {
                contentArr.Add(new JsonObject {
                    ["type"]     = "audio",
                    ["mimeType"] = mime,
                    ["data"]     = base64
                });
            }
            else
            {
                var uri  = $"slimfaas://tool-result/{Guid.NewGuid():N}";
                var name = string.IsNullOrWhiteSpace(r.FileName) ? "download" : r.FileName!;
                contentArr.Add(new JsonObject {
                    ["type"] = "resource",
                    ["resource"] = new JsonObject {
                        ["uri"]      = uri,
                        ["name"]     = name,
                        ["mimeType"] = mime,
                        ["size"]     = r.Bytes.Length,
                        ["blob"]     = base64
                    }
                });
            }
        }
        else
        {
            contentArr.Add(new JsonObject {
                ["type"] = "text",
                ["text"] = r.Text ?? ""
            });
        }

        return contentArr;
    }

    /// <summary>
    /// RESULT MCP complet { content: [...], structuredContent?: {...} }.
    /// Utiliser enableStructuredContent pour activer/désactiver l’inclusion de structuredContent.
    /// </summary>
    public static JsonObject BuildResult(ProxyCallResult r, bool enableStructuredContent)
    {
        var result = new JsonObject
        {
            ["content"] = Build(r)
        };

        if (enableStructuredContent && !r.IsBinary && TryExtractStructuredJson(r.MimeType, r.Text, out var structured))
        {
            result["structuredContent"] = structured;
        }

        return result;
    }

    /// <summary>
    /// Overload rétro-compat : structuredContent activé par défaut.
    /// </summary>
    public static JsonObject BuildResult(ProxyCallResult r) => BuildResult(r, true);

    private static bool TryExtractStructuredJson(string? mime, string? text, out JsonNode? node)
    {
        node = null;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var m = (mime ?? "").ToLowerInvariant();
        var looksJsonMime = m == "application/json" || m.EndsWith("+json") || m.StartsWith("application/ld+json");
        var looksMaybeJson = looksJsonMime || StartsWithJson(text);

        if (!looksMaybeJson) return false;
        try
        {
            var parsed  = JsonNode.Parse(text);
            if (parsed is null) return false;

            // ✅ règle MCP :
            // - scalaire => { "value": ... }
            // - array    => { "items": [...] }
            // - objet    => tel quel
            node = WrapStructuredNode(parsed);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool StartsWithJson(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) continue;
            return c == '{' || c == '[';
        }
        return false;
    }

    private static JsonNode WrapStructuredNode(JsonNode parsed)
    {
        switch (parsed)
        {
            case JsonArray arr:
                return new JsonObject { ["items"] = arr }; // { items: [...] }

            case JsonObject obj:
                return obj; // objet inchangé

            default:
                // JsonValue (string/number/bool/null)
                return new JsonObject { ["value"] = parsed }; // { value: ... }
        }
    }
}
