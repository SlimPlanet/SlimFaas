using Microsoft.EntityFrameworkCore;
using SlimFaasMcpGateway.Data.Entities;

namespace SlimFaasMcpGateway.Data;

public sealed class GatewayDbContext : DbContext
{
    public GatewayDbContext(DbContextOptions<GatewayDbContext> options) : base(options) { }

    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<GatewayConfiguration> Configurations => Set<GatewayConfiguration>();
    public DbSet<EnvironmentDeploymentMapping> EnvironmentMappings => Set<EnvironmentDeploymentMapping>();
    public DbSet<AuditRecord> AuditRecords => Set<AuditRecord>();

    // Helper methods for querying non-deleted entities (replaces HasQueryFilter for NativeAOT compatibility)
    public IQueryable<Tenant> ActiveTenants() => Tenants.Where(x => !x.IsDeleted);
    public IQueryable<GatewayConfiguration> ActiveConfigurations() => Configurations.Where(x => !x.IsDeleted);

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Tenant>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(256).IsRequired();
            b.Property(x => x.NormalizedName).HasMaxLength(256).IsRequired();
            b.Property(x => x.Description).HasMaxLength(1000);
            b.HasIndex(x => x.NormalizedName).IsUnique();
        });

        modelBuilder.Entity<GatewayConfiguration>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(256).IsRequired();
            b.Property(x => x.NormalizedName).HasMaxLength(256).IsRequired();
            b.Property(x => x.UpstreamMcpUrl).HasMaxLength(2048).IsRequired();
            b.Property(x => x.Description).HasMaxLength(1000);
            b.Property(x => x.CatalogOverrideYaml);
            b.Property(x => x.AuthPolicyYaml);
            b.Property(x => x.RateLimitPolicyYaml);
            b.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique();
        });

        modelBuilder.Entity<EnvironmentDeploymentMapping>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.EnvironmentName).HasMaxLength(64).IsRequired();
            b.HasIndex(x => new { x.TenantId, x.EnvironmentName, x.ConfigurationId }).IsUnique();
        });

        modelBuilder.Entity<AuditRecord>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.EntityType).HasMaxLength(64).IsRequired();
            b.Property(x => x.Author).HasMaxLength(256).IsRequired();
            b.HasIndex(x => new { x.EntityType, x.EntityId, x.Index }).IsUnique();
        });
    }
}
