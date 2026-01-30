using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SlimFaasMcpGateway.Data;
using SlimFaasMcpGateway.Dto;
using SlimFaasMcpGateway.Services;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Gateway;

public sealed record ResolvedGateway(
    Guid TenantId,
    string TenantName,
    Guid ConfigurationId,
    string ConfigurationName,
    string EnvironmentName,
    int DeployedAuditIndex,
    ConfigurationSnapshot Snapshot
);

public interface IGatewayResolver
{
    Task<ResolvedGateway> ResolveAsync(string tenantName, string environmentName, string configurationName, CancellationToken ct);
}

public sealed class GatewayResolver : IGatewayResolver
{
    private readonly GatewayDbContext _db;
    private readonly IAuditService _audit;

    public GatewayResolver(GatewayDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public async Task<ResolvedGateway> ResolveAsync(string tenantName, string environmentName, string configurationName, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(tenantName) || string.IsNullOrWhiteSpace(environmentName) || string.IsNullOrWhiteSpace(configurationName))
            throw new ApiException(404, "Not found.");

        var tNorm = tenantName.Trim().ToLowerInvariant();
        var eNorm = environmentName.Trim().ToLowerInvariant();
        var cNorm = configurationName.Trim().ToLowerInvariant();

        var tenant = await _db.Tenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.NormalizedName == tNorm, ct);

        if (tenant is null || tenant.IsDeleted)
            throw new ApiException(404, "Tenant not found.");

        var cfg = await _db.Configurations.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.TenantId == tenant.Id && x.NormalizedName == cNorm, ct);

        if (cfg is null || cfg.IsDeleted)
            throw new ApiException(404, "Configuration not found.");

        var mapping = await _db.EnvironmentMappings
            .FirstOrDefaultAsync(x => x.TenantId == tenant.Id && x.ConfigurationId == cfg.Id && x.EnvironmentName == eNorm, ct);

        if (mapping is null)
            throw new ApiException(404, "Environment mapping not found.");

        if (mapping.DeployedAuditIndex is null)
            throw new ApiException(404, "Not deployed.");

        var deployedIndex = mapping.DeployedAuditIndex.Value;

        var json = await _audit.ReconstructJsonAsync("configuration", cfg.Id, deployedIndex, ct);

        ConfigurationSnapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<ConfigurationSnapshot>(json, SlimFaasMcpGateway.Audit.AppJsonOptions.Default)
                ?? throw new InvalidOperationException("Null snapshot");
        }
        catch (Exception ex)
        {
            throw new ApiException(500, $"Failed to reconstruct configuration snapshot: {ex.Message}");
        }

        if (snapshot.IsDeleted)
            throw new ApiException(404, "Configuration deleted.");

        return new ResolvedGateway(
            tenant.Id,
            tenant.Name,
            cfg.Id,
            cfg.Name,
            eNorm,
            deployedIndex,
            snapshot
        );
    }
}
