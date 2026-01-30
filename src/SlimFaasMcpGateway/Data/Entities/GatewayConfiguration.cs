namespace SlimFaasMcpGateway.Data.Entities;

public sealed class GatewayConfiguration
{
    public Guid Id { get; set; }

    // Stored as an actual tenant id. Requests may omit TenantId, which is treated as "default".
    public Guid TenantId { get; set; }

    public string Name { get; set; } = string.Empty;

    public string NormalizedName { get; set; } = string.Empty;

    public string UpstreamMcpUrl { get; set; } = string.Empty;

    public string? Description { get; set; }

    // Encrypted at rest; never returned as plaintext.
    public string? DiscoveryJwtTokenProtected { get; set; }

    public string? CatalogOverrideYaml { get; set; }

    public bool EnforceAuthEnabled { get; set; }

    public string? AuthPolicyYaml { get; set; }

    public bool RateLimitEnabled { get; set; }

    public string? RateLimitPolicyYaml { get; set; }

    public int CatalogCacheTtlMinutes { get; set; }

    public int Version { get; set; }

    public bool IsDeleted { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    public DateTime? DeletedAtUtc { get; set; }
}
