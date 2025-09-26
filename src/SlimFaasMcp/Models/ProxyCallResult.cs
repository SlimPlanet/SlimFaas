using System.Text.Json.Serialization;

namespace SlimFaasMcp.Models;

public class ProxyCallResult
{
    public bool IsBinary { get; set; }

    public string? MimeType { get; set; }

    public string? FileName { get; set; }

    // Contenu texte (si non-binaire)
    public string? Text { get; set; }

    // Contenu binaire (si binaire)
    [JsonIgnore] // on ne sérialise pas en REST brut
    public byte[]? Bytes { get; set; }

    // Helper base64 (utilisé par /tools pour renvoyer un JSON lisible)
    public string? Base64 =>
        Bytes is null ? null : System.Convert.ToBase64String(Bytes);

    public int StatusCode { get; set; } = 200;
}
