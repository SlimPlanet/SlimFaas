using Microsoft.EntityFrameworkCore;
using SlimFaasMcpGateway.Data;
using SlimFaasMcpGateway.Data.Entities;
using SlimFaasMcpGateway.Dto;
using SlimFaasMcpGateway.Api.Validation;

namespace SlimFaasMcpGateway.Services;

public interface ITenantService
{
    Task<TenantDto> EnsureDefaultTenantAsync(CancellationToken ct);
    Task<IReadOnlyList<TenantListItemDto>> ListAsync(CancellationToken ct);
    Task<TenantDto> CreateAsync(TenantCreateRequest req, string author, CancellationToken ct);
    Task<TenantDto> UpdateAsync(Guid id, TenantUpdateRequest req, string author, CancellationToken ct);
    Task DeleteAsync(Guid id, string author, CancellationToken ct);
}

public sealed class TenantService : ITenantService
{
    public const string DefaultTenantName = "default";

    private readonly GatewayDbContext _db;
    private readonly IAuditService _audit;
    private readonly TimeProvider _time;

    public TenantService(GatewayDbContext db, IAuditService audit, TimeProvider time)
    {
        _db = db;
        _audit = audit;
        _time = time;
    }

    public async Task<TenantDto> EnsureDefaultTenantAsync(CancellationToken ct)
    {
        var norm = DefaultTenantName;
        var existing = await _db.Tenants.IgnoreQueryFilters()
            .FirstOrDefaultAsync(x => x.NormalizedName == norm, ct);

        if (existing is not null)
        {
            if (existing.IsDeleted)
            {
                existing.IsDeleted = false;
                existing.DeletedAtUtc = null;
                existing.UpdatedAtUtc = _time.GetUtcNow().UtcDateTime;
                _db.Update(existing);
                await _db.SaveChangesAsync(ct);
            }

            return new TenantDto(existing.Id, existing.Name, existing.Description, existing.CreatedAtUtc, existing.UpdatedAtUtc);
        }

        var now = _time.GetUtcNow().UtcDateTime;

        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = DefaultTenantName,
            NormalizedName = DefaultTenantName,
            Description = "Default tenant",
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        await _audit.AppendAsync("tenant", tenant.Id, "system", System.Text.Json.JsonSerializer.Serialize(tenant, SlimFaasMcpGateway.Audit.AppJsonOptions.Default), ct);

        return new TenantDto(tenant.Id, tenant.Name, tenant.Description, tenant.CreatedAtUtc, tenant.UpdatedAtUtc);
    }

    public async Task<IReadOnlyList<TenantListItemDto>> ListAsync(CancellationToken ct)
    {
        return await _db.Tenants
            .OrderBy(x => x.Name)
            .Select(x => new TenantListItemDto(x.Id, x.Name, x.Description))
            .ToListAsync(ct);
    }

    public async Task<TenantDto> CreateAsync(TenantCreateRequest req, string author, CancellationToken ct)
    {
        InputValidators.ValidateTenantName(req.Name);
        InputValidators.ValidateDescription(req.Description);

        var norm = InputValidators.NormalizeName(req.Name);

        var exists = await _db.Tenants.IgnoreQueryFilters()
            .AnyAsync(x => x.NormalizedName == norm && !x.IsDeleted, ct);

        if (exists)
            throw new ApiException(409, "Tenant name already exists.");

        var now = _time.GetUtcNow().UtcDateTime;
        var tenant = new Tenant
        {
            Id = Guid.NewGuid(),
            Name = req.Name.Trim(),
            NormalizedName = norm,
            Description = req.Description?.Trim(),
            IsDeleted = false,
            CreatedAtUtc = now,
            UpdatedAtUtc = now
        };

        _db.Tenants.Add(tenant);
        await _db.SaveChangesAsync(ct);

        await _audit.AppendAsync("tenant", tenant.Id, author, System.Text.Json.JsonSerializer.Serialize(tenant, SlimFaasMcpGateway.Audit.AppJsonOptions.Default), ct);

        return new TenantDto(tenant.Id, tenant.Name, tenant.Description, tenant.CreatedAtUtc, tenant.UpdatedAtUtc);
    }

    public async Task<TenantDto> UpdateAsync(Guid id, TenantUpdateRequest req, string author, CancellationToken ct)
    {
        InputValidators.ValidateDescription(req.Description);

        var tenant = await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (tenant is null || tenant.IsDeleted) throw new ApiException(404, "Tenant not found.");

        tenant.Description = req.Description?.Trim();
        tenant.UpdatedAtUtc = _time.GetUtcNow().UtcDateTime;

        _db.Update(tenant);
        await _db.SaveChangesAsync(ct);

        await _audit.AppendAsync("tenant", tenant.Id, author, System.Text.Json.JsonSerializer.Serialize(tenant, SlimFaasMcpGateway.Audit.AppJsonOptions.Default), ct);

        return new TenantDto(tenant.Id, tenant.Name, tenant.Description, tenant.CreatedAtUtc, tenant.UpdatedAtUtc);
    }

    public async Task DeleteAsync(Guid id, string author, CancellationToken ct)
    {
        var tenant = await _db.Tenants.IgnoreQueryFilters().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (tenant is null || tenant.IsDeleted) throw new ApiException(404, "Tenant not found.");

        tenant.IsDeleted = true;
        tenant.DeletedAtUtc = _time.GetUtcNow().UtcDateTime;
        tenant.UpdatedAtUtc = tenant.DeletedAtUtc.Value;

        _db.Update(tenant);
        await _db.SaveChangesAsync(ct);

        await _audit.AppendAsync("tenant", tenant.Id, author, System.Text.Json.JsonSerializer.Serialize(tenant, SlimFaasMcpGateway.Audit.AppJsonOptions.Default), ct);
    }
}
