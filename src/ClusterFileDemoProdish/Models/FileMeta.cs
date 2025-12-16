using System.Text.Json.Serialization;

namespace ClusterFileDemoProdish.Models;

public sealed record FileMeta
{
    public required string Id { get; init; }
    public string ContentType { get; init; } = "application/octet-stream";
    public string? FileName { get; init; }
    public long SizeBytes { get; init; }
    public string? Sha256Hex { get; init; }

    public long CreatedUtcMs { get; init; }
    public long? ExpiresUtcMs { get; init; }

    [JsonIgnore]
    public bool IsExpired
    {
        get
        {
            if (ExpiresUtcMs is null) return false;
            return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() >= ExpiresUtcMs.Value;
        }
    }
}
