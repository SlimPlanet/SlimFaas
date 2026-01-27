using System.Text.Json.Nodes;
using SlimFaasMcpGateway.Audit;

namespace SlimFaasMcpGateway.Dto;

public sealed record ApiErrorDto(string Error);

public sealed record TenantCreateRequest(string Name, string? Description);

public sealed record TenantUpdateRequest(string Description);

public sealed record TenantDto(Guid Id, string Name, string? Description, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record TenantListItemDto(Guid Id, string Name, string? Description);

public sealed record UpstreamMcpServerDto(
    string ToolPrefix,
    string BaseUrl,
    string? DiscoveryJwtToken, // plaintext for input, never returned
    bool HasDiscoveryJwtToken  // output only
);

public sealed record ConfigurationCreateOrUpdateRequest(
    string Name,
    Guid? TenantId,
    string? UpstreamMcpUrl, // deprecated, kept for backward compatibility
    IReadOnlyList<UpstreamMcpServerDto>? UpstreamServers, // new multi-upstream support
    string? Description,
    string? DiscoveryJwtToken, // deprecated, kept for backward compatibility
    string? CatalogOverrideYaml,
    bool EnforceAuthEnabled,
    string? AuthPolicyYaml,
    bool RateLimitEnabled,
    string? RateLimitPolicyYaml,
    int CatalogCacheTtlMinutes
);

public sealed record ConfigurationDto(
    Guid Id,
    Guid? TenantId,
    string TenantName,
    string Name,
    string? UpstreamMcpUrl, // deprecated, kept for backward compatibility
    IReadOnlyList<UpstreamMcpServerDto>? UpstreamServers, // new multi-upstream support
    string? Description,
    bool HasDiscoveryJwtToken, // deprecated
    string? CatalogOverrideYaml,
    bool EnforceAuthEnabled,
    string? AuthPolicyYaml,
    bool RateLimitEnabled,
    string? RateLimitPolicyYaml,
    int CatalogCacheTtlMinutes,
    int Version,
    DateTime CreatedAtUtc,
    DateTime UpdatedAtUtc
);

public sealed record ConfigurationListItemDto(
    Guid Id,
    string TenantName,
    string Name,
    string GatewayUrl,
    DateTime CreatedAtUtc,
    string DefaultDeploymentEnvironment
);

public sealed record LoadCatalogResponseDto(string CatalogYaml);

public sealed record EnvironmentListDto(IReadOnlyList<string> Environments);

public sealed record DeploymentStateDto(string EnvironmentName, int? DeployedAuditIndex);

public sealed record DeploymentOverviewDto(
    Guid ConfigurationId,
    string TenantName,
    string ConfigurationName,
    IReadOnlyList<DeploymentStateDto> Environments,
    IReadOnlyList<AuditHistoryItemDto> History
);

public sealed record SetDeploymentRequest(int? DeployedAuditIndex);

public sealed record AuditHistoryItemDto(int Index, long ModifiedAtUtc, string Author);

public sealed record AuditSideDto(int Index, long ModifiedAtUtc, string Author);

public sealed record AuditDiffDto(AuditSideDto From, AuditSideDto To, IReadOnlyList<JsonPatch.Op> Patch);

public sealed record AuditTextDiffDto(AuditSideDto From, AuditSideDto To, TextDiff.UnifiedDiff UnifiedDiff);

public sealed record GatewayErrorDto(int StatusCode, string Error);
