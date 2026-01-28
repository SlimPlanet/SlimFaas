namespace SlimFaasMcpGateway.Data.Entities;

public sealed class EnvironmentDeploymentMapping
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public string EnvironmentName { get; set; } = string.Empty;

    public Guid ConfigurationId { get; set; }

    // null means "not deployed"
    public int? DeployedAuditIndex { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }
}
