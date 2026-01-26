using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SlimFaasMcpGateway.Data;
using SlimFaasMcpGateway.Data.Entities;
using SlimFaasMcpGateway.Dto;
using SlimFaasMcpGateway.Options;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Services;

public interface IDeploymentService
{
    Task<DeploymentOverviewDto> GetOverviewAsync(Guid configurationId, CancellationToken ct);
    Task SetDeploymentAsync(Guid configurationId, string environmentName, int? deployedAuditIndex, string author, CancellationToken ct);
}

public sealed class DeploymentService : IDeploymentService
{
    private readonly GatewayDbContext _db;
    private readonly IAuditService _audit;
    private readonly TimeProvider _time;
    private readonly GatewayOptions _options;

    public DeploymentService(GatewayDbContext db, IAuditService audit, TimeProvider time, IOptions<GatewayOptions> options)
    {
        _db = db;
        _audit = audit;
        _time = time;
        _options = options.Value;
    }

    public async Task<DeploymentOverviewDto> GetOverviewAsync(Guid configurationId, CancellationToken ct)
    {
        var cfg = await (from c in _db.Configurations.IgnoreQueryFilters()
                         join t in _db.Tenants.IgnoreQueryFilters() on c.TenantId equals t.Id
                         where c.Id == configurationId
                         select new { c, t }).FirstOrDefaultAsync(ct);

        if (cfg is null || cfg.c.IsDeleted || cfg.t.IsDeleted) throw new ApiException(404, "Configuration not found.");

        var envs = _options.GetEnvironmentsOrDefault().Select(x => x.ToLowerInvariant()).ToList();

        var mappings = await _db.EnvironmentMappings
            .Where(x => x.ConfigurationId == configurationId && x.TenantId == cfg.t.Id)
            .ToListAsync(ct);

        var states = envs.Select(env =>
        {
            var m = mappings.FirstOrDefault(x => x.EnvironmentName == env);
            return new DeploymentStateDto(env, m?.DeployedAuditIndex);
        }).ToList();

        var history = await _audit.ListAsync("configuration", configurationId, ct);

        return new DeploymentOverviewDto(configurationId, cfg.t.Name, cfg.c.Name, states, history);
    }

    public async Task SetDeploymentAsync(Guid configurationId, string environmentName, int? deployedAuditIndex, string author, CancellationToken ct)
    {
        environmentName = (environmentName ?? "").Trim().ToLowerInvariant();

        var allowed = _options.GetEnvironmentsOrDefault().Select(x => x.ToLowerInvariant()).ToHashSet();
        if (!allowed.Contains(environmentName))
            throw new ApiException(400, "Unknown environment.");

        var cfg = await _db.Configurations.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == configurationId, ct);
        if (cfg is null || cfg.IsDeleted) throw new ApiException(404, "Configuration not found.");

        var tenant = await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == cfg.TenantId, ct);
        if (tenant is null || tenant.IsDeleted) throw new ApiException(404, "Tenant not found.");

        // Validate deployed index exists if not null
        if (deployedAuditIndex is not null)
        {
            var exists = await _db.AuditRecords.AnyAsync(x => x.EntityType == "configuration" && x.EntityId == configurationId && x.Index == deployedAuditIndex.Value, ct);
            if (!exists) throw new ApiException(400, "DeployedAuditIndex does not exist in configuration history.");
        }

        var mapping = await _db.EnvironmentMappings
            .FirstOrDefaultAsync(x => x.TenantId == tenant.Id && x.ConfigurationId == configurationId && x.EnvironmentName == environmentName, ct);

        var now = _time.GetUtcNow().UtcDateTime;

        if (mapping is null)
        {
            mapping = new EnvironmentDeploymentMapping
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                ConfigurationId = configurationId,
                EnvironmentName = environmentName,
                DeployedAuditIndex = deployedAuditIndex,
                CreatedAtUtc = now,
                UpdatedAtUtc = now
            };
            _db.EnvironmentMappings.Add(mapping);
        }
        else
        {
            mapping.DeployedAuditIndex = deployedAuditIndex;
            mapping.UpdatedAtUtc = now;
            _db.EnvironmentMappings.Update(mapping);
        }

        await _db.SaveChangesAsync(ct);

        var mapJson = System.Text.Json.JsonSerializer.Serialize(mapping, SlimFaasMcpGateway.Audit.AppJsonOptions.Default);
        await _audit.AppendAsync("environmentMapping", mapping.Id, author, mapJson, ct);
    }
}
