namespace SlimFaasMcpGateway.Options;

public sealed class GatewayOptions
{
    public string[]? Environments { get; set; }

    public List<string> GetEnvironmentsOrDefault()
    {
        var envs = Environments?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).ToList() ?? new();
        if (envs.Count == 0) return new List<string> { "prod" };
        return envs;
    }
}

public sealed class SecurityOptions
{
    public string DiscoveryTokenEncryptionKey { get; set; } = "";
}

