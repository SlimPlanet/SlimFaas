namespace SlimFaasMcpGateway.Data.Entities;

/// <summary>
/// Represents an upstream MCP server with a tool prefix for routing
/// </summary>
public sealed class UpstreamMcpServer
{
    public Guid Id { get; set; }

    public Guid ConfigurationId { get; set; }

    /// <summary>
    /// Prefix for all tools from this upstream (e.g., "slack_", "github_")
    /// Used for routing tool calls to the correct upstream
    /// </summary>
    public string ToolPrefix { get; set; } = string.Empty;

    /// <summary>
    /// Base URL of the upstream MCP server
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional JWT token for discovery (encrypted)
    /// </summary>
    public string? DiscoveryJwtTokenProtected { get; set; }

    /// <summary>
    /// Display order in the list
    /// </summary>
    public int DisplayOrder { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public DateTime UpdatedAtUtc { get; set; }

    // Navigation property
    public GatewayConfiguration? Configuration { get; set; }
}
