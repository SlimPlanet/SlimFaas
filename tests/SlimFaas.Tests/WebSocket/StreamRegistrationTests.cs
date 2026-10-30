using SlimFaas.Streams;
using SlimFaas.WebSocket;

namespace SlimFaas.Tests.WebSocket;

public sealed class StreamRegistrationTests
{
    [Fact]
    public void Global_fingerprints_preserve_local_order_independence_and_detect_retry_or_dependency_conflicts()
    {
        var first = new WebSocketFunctionConfiguration
        {
            DependsOn = ["b", "a"],
            SubscribeStreams = [new() { Name = "orders" }, new() { Name = "notifications" }]
        };
        var reordered = new WebSocketFunctionConfiguration
        {
            DependsOn = ["a", "b"],
            SubscribeStreams = [new() { Name = "notifications" }, new() { Name = "orders" }]
        };
        Assert.Equal(StreamConfiguration.Fingerprint(first), StreamConfiguration.Fingerprint(reordered));
        reordered.Configuration = "{\"DefaultAsync\":{\"HttpTimeout\":42}}";
        Assert.NotEqual(StreamConfiguration.Fingerprint(first), StreamConfiguration.Fingerprint(reordered));
        reordered.Configuration = first.Configuration;
        reordered.DependsOn = ["other"];
        Assert.NotEqual(StreamConfiguration.Fingerprint(first), StreamConfiguration.Fingerprint(reordered));
    }
}
