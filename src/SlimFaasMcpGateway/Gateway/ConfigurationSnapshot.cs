namespace SlimFaasMcpGateway.Gateway;

public sealed record ConfigurationSnapshot(
    Guid Id,
    Guid TenantId,
    string TenantName,
    string Name,
    string NormalizedName,
    string UpstreamMcpUrl,
    string? Description,
    string? DiscoveryJwtTokenProtected,
    string? CatalogOverrideYaml,
    bool EnforceAuthEnabled,
    string? AuthPolicyYaml,
    bool RateLimitEnabled,
    string? RateLimitPolicyYaml,
    int CatalogCacheTtlMinutes,
    int Version,
    bool IsDeleted
);
