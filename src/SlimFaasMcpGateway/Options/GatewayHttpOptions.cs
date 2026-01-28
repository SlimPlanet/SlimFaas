using System.ComponentModel.DataAnnotations;

namespace SlimFaasMcpGateway.Options;

public sealed class GatewayHttpOptions
{
    public HttpClientOptions HttpClient { get; set; } = new();
}

public sealed class HttpClientOptions
{
    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;
}
