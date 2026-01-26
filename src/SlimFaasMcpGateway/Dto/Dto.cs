using System.Text.Json.Nodes;
using SlimFaasMcpGateway.Audit;

namespace SlimFaasMcpGateway.Dto;

public sealed record ApiErrorDto(string Error);

public sealed record TenantCreateRequest(string Name, string? Description);

public sealed record TenantUpdateRequest(string Description);

public sealed record TenantDto(Guid Id, string Name, string? Description, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);

public sealed record TenantListItemDto(Guid Id, string Name, string? Description);

public sealed record ConfigurationCreateOrUpdateRequest(
    string Name,
    Guid? TenantId,
    string UpstreamMcpUrl,
    string? Description,
    string? DiscoveryJwtToken, // plaintext; stored encrypted; never returned
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
    string UpstreamMcpUrl,
    string? Description,
    bool HasDiscoveryJwtToken,
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

public sealed record GatewayErrorDto(int StatusCode, string Error);
