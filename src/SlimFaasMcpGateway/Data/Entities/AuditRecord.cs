namespace SlimFaasMcpGateway.Data.Entities;

public sealed class AuditRecord
{
    public Guid Id { get; set; }

    public string EntityType { get; set; } = string.Empty;

    public Guid EntityId { get; set; }

    // Monotonically increasing per (EntityType, EntityId), starting at 0
    public int Index { get; set; }

    // Unix timestamp UTC
    public long ModifiedAtUtc { get; set; }

    public string Author { get; set; } = "unknown";

    // Only set for index == 0
    public string? FullJsonSnapshot { get; set; }

    // Only set for index > 0
    public string? JsonPatch { get; set; }
}
