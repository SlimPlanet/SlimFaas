using System.Text.Json.Serialization;
using SlimFaasMcpGateway.Dto;
using SlimFaasMcpGateway.Gateway;
using SlimFaasMcpGateway.Audit;
using SlimFaasMcpGateway.Data.Entities;

namespace SlimFaasMcpGateway.Serialization;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(EnvironmentListDto))]
[JsonSerializable(typeof(TenantCreateRequest))]
[JsonSerializable(typeof(TenantUpdateRequest))]
[JsonSerializable(typeof(TenantDto))]
[JsonSerializable(typeof(TenantListItemDto))]
[JsonSerializable(typeof(ConfigurationCreateOrUpdateRequest))]
[JsonSerializable(typeof(ConfigurationDto))]
[JsonSerializable(typeof(ConfigurationListItemDto))]
[JsonSerializable(typeof(LoadCatalogResponseDto))]
[JsonSerializable(typeof(DeploymentOverviewDto))]
[JsonSerializable(typeof(DeploymentStateDto))]
[JsonSerializable(typeof(SetDeploymentRequest))]
[JsonSerializable(typeof(AuditHistoryItemDto))]
[JsonSerializable(typeof(AuditSideDto))]
[JsonSerializable(typeof(AuditDiffDto))]
[JsonSerializable(typeof(AuditTextDiffDto))]
[JsonSerializable(typeof(TextDiff.UnifiedDiff))]
[JsonSerializable(typeof(TextDiff.DiffLine))]
[JsonSerializable(typeof(List<TextDiff.DiffLine>))]
[JsonSerializable(typeof(List<AuditHistoryItemDto>))]
[JsonSerializable(typeof(List<ConfigurationListItemDto>))]
[JsonSerializable(typeof(List<TenantListItemDto>))]
[JsonSerializable(typeof(ConfigurationSnapshot))]
[JsonSerializable(typeof(JsonPatch.Op))]
[JsonSerializable(typeof(List<JsonPatch.Op>))]
[JsonSerializable(typeof(Tenant))]
[JsonSerializable(typeof(GatewayConfiguration))]
[JsonSerializable(typeof(EnvironmentDeploymentMapping))]
public partial class ApiJsonContext : JsonSerializerContext
{
}
