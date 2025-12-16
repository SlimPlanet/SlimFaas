using DotNext.Net.Cluster.Messaging;

namespace ClusterFileDemoProdish.Cluster;

public sealed class ClusterMessagingRegistrationService : IHostedService
{
    private readonly IMessageBus _bus;
    private readonly ClusterMessagingChannel _channel;
    private readonly ILogger<ClusterMessagingRegistrationService> _logger;

    public ClusterMessagingRegistrationService(IMessageBus bus, ClusterMessagingChannel channel, ILogger<ClusterMessagingRegistrationService> logger)
    {
        _bus = bus;
        _channel = channel;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Registering cluster messaging channel");
        _bus.AddListener(_channel);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Unregistering cluster messaging channel");
        _bus.RemoveListener(_channel);
        return Task.CompletedTask;
    }
}
