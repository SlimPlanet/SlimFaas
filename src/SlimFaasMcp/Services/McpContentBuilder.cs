using System.Text.Json.Nodes;
using SlimFaasMcp.Models;

namespace SlimFaasMcp.Services;

public static class McpContentBuilder
{
    /// <summary>
    /// Construit le tableau MCP "content" à partir d'un ProxyCallResult.
    /// Règles:
    ///  - image/*  -> { type:"image",  mimeType, data(base64) }
    ///  - audio/*  -> { type:"audio",  mimeType, data(base64) }
    ///  - autres binaires -> { type:"resource", resource:{ uri,name,mimeType,size,blob } }
    ///  - texte / json -> { type:"text", text }
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
}
