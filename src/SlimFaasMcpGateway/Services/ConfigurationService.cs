using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SlimFaasMcpGateway.Auth;
using SlimFaasMcpGateway.Data;
using SlimFaasMcpGateway.Data.Entities;
using SlimFaasMcpGateway.Dto;
using SlimFaasMcpGateway.Gateway;
using SlimFaasMcpGateway.Options;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Services;

public interface IConfigurationService
{
    Task<IReadOnlyList<ConfigurationListItemDto>> ListAsync(CancellationToken ct);
    Task<ConfigurationDto> GetAsync(Guid id, CancellationToken ct);
    Task<ConfigurationDto> CreateAsync(ConfigurationCreateOrUpdateRequest req, string author, CancellationToken ct);
    Task<ConfigurationDto> UpdateAsync(Guid id, ConfigurationCreateOrUpdateRequest req, string author, CancellationToken ct);
    Task DeleteAsync(Guid id, string author, CancellationToken ct);
}

public sealed class ConfigurationService : IConfigurationService
{
    private readonly GatewayDbContext _db;
    private readonly ITenantService _tenants;
    private readonly IAuditService _audit;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _time;
    private readonly GatewayOptions _gatewayOptions;

    public ConfigurationService(
        GatewayDbContext db,
        ITenantService tenants,
        IAuditService audit,
        ISecretProtector protector,
        TimeProvider time,
        IOptions<GatewayOptions> gatewayOptions)
    {
        _db = db;
        _tenants = tenants;
        _audit = audit;
        _protector = protector;
        _time = time;
        _gatewayOptions = gatewayOptions.Value;
    }

    public async Task<IReadOnlyList<ConfigurationListItemDto>> ListAsync(CancellationToken ct)
    {
        var envs = _gatewayOptions.GetEnvironmentsOrDefault();
        var defaultEnv = envs[0];

        var list = await (from c in _db.Configurations
                          join t in _db.Tenants on c.TenantId equals t.Id
                          orderby t.Name, c.Name
                          select new
                          {
                              c.Id,
                              TenantName = t.Name,
                              c.Name,
                              c.CreatedAtUtc
                          }).ToListAsync(ct);

        return list.Select(x =>
        {
            var gatewayUrl = $"/gateway/mcp/{Uri.EscapeDataString(x.TenantName)}/{Uri.EscapeDataString(defaultEnv)}/{Uri.EscapeDataString(x.Name)}";
            return new ConfigurationListItemDto(x.Id, x.TenantName, x.Name, gatewayUrl, x.CreatedAtUtc, defaultEnv);
        }).ToList();
    }

    public async Task<ConfigurationDto> GetAsync(Guid id, CancellationToken ct)
    {
        var cfg = await (from c in _db.Configurations
                         join t in _db.Tenants on c.TenantId equals t.Id
                         where c.Id == id
                         select new { c, t }).FirstOrDefaultAsync(ct);

        if (cfg is null) throw new ApiException(404, "Configuration not found.");

        return ToDto(cfg.c, cfg.t);
    }

    public async Task<ConfigurationDto> CreateAsync(ConfigurationCreateOrUpdateRequest req, string author, CancellationToken ct)
    {
        Validate(req);

        var tenant = await ResolveTenantAsync(req.TenantId, ct);

        var norm = InputValidators.NormalizeName(req.Name);

        var exists = await _db.Configurations.IgnoreQueryFilters()
            .AnyAsync(x => x.TenantId == tenant.Id && x.NormalizedName == norm && !x.IsDeleted, ct);

        if (exists)
            throw new ApiException(409, "Configuration name already exists in this tenant.");

        var now = _time.GetUtcNow().UtcDateTime;

        var cfg = new GatewayConfiguration
        {
            Id = Guid.NewGuid(),
            TenantId = tenant.Id,
            Name = req.Name.Trim(),
            NormalizedName = norm,
            UpstreamMcpUrl = req.UpstreamMcpUrl.Trim(),
            Description = req.Description?.Trim(),
            CatalogOverrideYaml = req.CatalogOverrideYaml,
            EnforceAuthEnabled = req.EnforceAuthEnabled,
            AuthPolicyYaml = req.AuthPolicyYaml,
            RateLimitEnabled = req.RateLimitEnabled,
            RateLimitPolicyYaml = req.RateLimitPolicyYaml,
            CatalogCacheTtlMinutes = req.CatalogCacheTtlMinutes,
            Version = 1,
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        ApplyDiscoveryToken(cfg, req.DiscoveryJwtToken);

        _db.Configurations.Add(cfg);
        await _db.SaveChangesAsync(ct);

        // Audit configuration snapshot
        var snapJson = System.Text.Json.JsonSerializer.Serialize(ToSnapshot(cfg, tenant), SlimFaasMcpGateway.Audit.AppJsonOptions.Default);
        var append = await _audit.AppendAsync("configuration", cfg.Id, author, snapJson, ct);

        // Default deployment to first environment
        var defaultEnv = _gatewayOptions.GetEnvironmentsOrDefault()[0].ToLowerInvariant();
        await UpsertDeploymentAsync(cfg, tenant, defaultEnv, append.Index, author, ct);

        return ToDto(cfg, tenant);
    }

    public async Task<ConfigurationDto> UpdateAsync(Guid id, ConfigurationCreateOrUpdateRequest req, string author, CancellationToken ct)
    {
        Validate(req);

        var cfg = await _db.Configurations.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (cfg is null || cfg.IsDeleted) throw new ApiException(404, "Configuration not found.");

        var tenant = await ResolveTenantAsync(req.TenantId, ct);

        var norm = InputValidators.NormalizeName(req.Name);

        var exists = await _db.Configurations.IgnoreQueryFilters()
            .AnyAsync(x => x.Id != id && x.TenantId == tenant.Id && x.NormalizedName == norm && !x.IsDeleted, ct);

        if (exists)
            throw new ApiException(409, "Configuration name already exists in this tenant.");

        cfg.TenantId = tenant.Id;
        cfg.Name = req.Name.Trim();
        cfg.NormalizedName = norm;
        cfg.UpstreamMcpUrl = req.UpstreamMcpUrl.Trim();
        cfg.Description = req.Description?.Trim();
        cfg.CatalogOverrideYaml = req.CatalogOverrideYaml;
        cfg.EnforceAuthEnabled = req.EnforceAuthEnabled;
        cfg.AuthPolicyYaml = req.AuthPolicyYaml;
        cfg.RateLimitEnabled = req.RateLimitEnabled;
        cfg.RateLimitPolicyYaml = req.RateLimitPolicyYaml;
        cfg.CatalogCacheTtlMinutes = req.CatalogCacheTtlMinutes;
        cfg.Version = Math.Max(1, cfg.Version) + 1;
        cfg.UpdatedAtUtc = _time.GetUtcNow().UtcDateTime;

        ApplyDiscoveryToken(cfg, req.DiscoveryJwtToken);

        _db.Update(cfg);
        await _db.SaveChangesAsync(ct);

        var snapJson = System.Text.Json.JsonSerializer.Serialize(ToSnapshot(cfg, tenant), SlimFaasMcpGateway.Audit.AppJsonOptions.Default);
        var append = await _audit.AppendAsync("configuration", cfg.Id, author, snapJson, ct);

        // Auto-move first environment to latest
        var defaultEnv = _gatewayOptions.GetEnvironmentsOrDefault()[0].ToLowerInvariant();
        await UpsertDeploymentAsync(cfg, tenant, defaultEnv, append.Index, author, ct);

        return ToDto(cfg, tenant);
    }

    public async Task DeleteAsync(Guid id, string author, CancellationToken ct)
    {
        var cfg = await _db.Configurations.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (cfg is null || cfg.IsDeleted) throw new ApiException(404, "Configuration not found.");

        var tenant = await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == cfg.TenantId, ct);
        if (tenant is null || tenant.IsDeleted) throw new ApiException(404, "Tenant not found.");

        cfg.IsDeleted = true;
        cfg.DeletedAtUtc = _time.GetUtcNow().UtcDateTime;
        cfg.UpdatedAtUtc = cfg.DeletedAtUtc.Value;
        _db.Update(cfg);
        await _db.SaveChangesAsync(ct);

        var snapJson = System.Text.Json.JsonSerializer.Serialize(ToSnapshot(cfg, tenant), SlimFaasMcpGateway.Audit.AppJsonOptions.Default);
        await _audit.AppendAsync("configuration", cfg.Id, author, snapJson, ct);
    }

    private async Task<Tenant> ResolveTenantAsync(Guid? tenantId, CancellationToken ct)
    {
        // TenantId omitted -> default tenant
        if (tenantId is null)
        {
            var def = await _tenants.EnsureDefaultTenantAsync(ct);
            var tenant = await _db.Tenants.IgnoreQueryFilters().FirstAsync(x => x.Id == def.Id, ct);
            return tenant;
        }

        var t = await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == tenantId.Value, ct);
        if (t is null || t.IsDeleted) throw new ApiException(404, "Tenant not found.");
        return t;
    }

    private static void Validate(ConfigurationCreateOrUpdateRequest req)
    {
        InputValidators.ValidateConfigurationName(req.Name);
        InputValidators.ValidateAbsoluteHttpUrl(req.UpstreamMcpUrl, "UpstreamMcpUrl");
        InputValidators.ValidateDescription(req.Description);
        InputValidators.ValidateCatalogCacheTtl(req.CatalogCacheTtlMinutes);

        InputValidators.ValidateYamlIfPresent(req.CatalogOverrideYaml, "CatalogOverrideYaml");
        InputValidators.RequireYamlWhenEnabled(req.EnforceAuthEnabled, req.AuthPolicyYaml, "AuthPolicyYaml");
        InputValidators.RequireYamlWhenEnabled(req.RateLimitEnabled, req.RateLimitPolicyYaml, "RateLimitPolicyYaml");

        if (req.EnforceAuthEnabled && !string.IsNullOrWhiteSpace(req.AuthPolicyYaml))
            _ = AuthPolicyParser.Parse(req.AuthPolicyYaml);

        if (req.RateLimitEnabled && !string.IsNullOrWhiteSpace(req.RateLimitPolicyYaml))
            _ = RateLimitPolicyParser.Parse(req.RateLimitPolicyYaml);
    }

    private void ApplyDiscoveryToken(GatewayConfiguration cfg, string? discoveryTokenPlaintext)
    {
        if (discoveryTokenPlaintext is null)
            return; // keep existing

        if (string.IsNullOrWhiteSpace(discoveryTokenPlaintext))
        {
            cfg.DiscoveryJwtTokenProtected = null; // clear
            return;
        }

        cfg.DiscoveryJwtTokenProtected = _protector.Protect(discoveryTokenPlaintext.Trim());
    }

    private static ConfigurationSnapshot ToSnapshot(GatewayConfiguration cfg, Tenant tenant) =>
        new ConfigurationSnapshot(
            cfg.Id,
            tenant.Id,
            tenant.Name,
            cfg.Name,
            cfg.NormalizedName,
            cfg.UpstreamMcpUrl,
            cfg.Description,
            cfg.DiscoveryJwtTokenProtected,
            cfg.CatalogOverrideYaml,
            cfg.EnforceAuthEnabled,
            cfg.AuthPolicyYaml,
            cfg.RateLimitEnabled,
            cfg.RateLimitPolicyYaml,
            cfg.CatalogCacheTtlMinutes,
            cfg.Version,
            cfg.IsDeleted
        );

    private ConfigurationDto ToDto(GatewayConfiguration cfg, Tenant tenant)
    {
        var isDefault = string.Equals(tenant.NormalizedName, TenantService.DefaultTenantName, StringComparison.OrdinalIgnoreCase);
        return new ConfigurationDto(
            cfg.Id,
            isDefault ? null : tenant.Id,
            tenant.Name,
            cfg.Name,
            cfg.UpstreamMcpUrl,
            cfg.Description,
            HasDiscoveryJwtToken: !string.IsNullOrWhiteSpace(cfg.DiscoveryJwtTokenProtected),
            cfg.CatalogOverrideYaml,
            cfg.EnforceAuthEnabled,
            cfg.AuthPolicyYaml,
            cfg.RateLimitEnabled,
            cfg.RateLimitPolicyYaml,
            cfg.CatalogCacheTtlMinutes,
            cfg.Version,
            cfg.CreatedAtUtc,
            cfg.UpdatedAtUtc
        );
    }

    private async Task UpsertDeploymentAsync(GatewayConfiguration cfg, Tenant tenant, string environmentName, int deployedAuditIndex, string author, CancellationToken ct)
    {
        environmentName = environmentName.Trim().ToLowerInvariant();

        var mapping = await _db.EnvironmentMappings
            .FirstOrDefaultAsync(x => x.TenantId == tenant.Id && x.ConfigurationId == cfg.Id && x.EnvironmentName == environmentName, ct);

        var now = _time.GetUtcNow().UtcDateTime;

        if (mapping is null)
        {
            mapping = new EnvironmentDeploymentMapping
            {
                Id = Guid.NewGuid(),
                TenantId = tenant.Id,
                EnvironmentName = environmentName,
                ConfigurationId = cfg.Id,
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

        // Audit environment mapping snapshot
        var mapJson = System.Text.Json.JsonSerializer.Serialize(mapping, SlimFaasMcpGateway.Audit.AppJsonOptions.Default);
        await _audit.AppendAsync("environmentMapping", mapping.Id, author, mapJson, ct);
    }
}
