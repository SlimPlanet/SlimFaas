using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SlimFaasKafka.Config;

namespace SlimFaasKafka.Tests;

public class ConfigurationBindingTests
{
    [Fact]
    public void KafkaOptions_AreBoundFromConfiguration()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["Kafka:BootstrapServers"] = "kafka:9092",
            ["Kafka:ClientId"] = "slimfaaskafka-test",
            ["Kafka:CheckIntervalSeconds"] = "7",
            ["Kafka:KafkaTimeoutSeconds"] = "9",
            ["Kafka:AllowAutoCreateTopics"] = "true"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings!)
            .Build();

        var services = new ServiceCollection();
        services.Configure<KafkaOptions>(config.GetSection("Kafka"));
        var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<KafkaOptions>>().Value;

        // Assert
        Assert.Equal("kafka:9092", options.BootstrapServers);
        Assert.Equal("slimfaaskafka-test", options.ClientId);
        Assert.Equal(7, options.CheckIntervalSeconds);
        Assert.Equal(9, options.KafkaTimeoutSeconds);
        Assert.True(options.AllowAutoCreateTopics);
    }

    [Fact]
    public void BindingsOptions_AreBoundFromConfiguration()
    {
        // Arrange
        var inMemorySettings = new Dictionary<string, string?>
        {
            ["SlimFaasKafka:Bindings:0:Topic"] = "fibo-public",
            ["SlimFaasKafka:Bindings:0:ConsumerGroupId"] = "fibonacci-group",
            ["SlimFaasKafka:Bindings:0:FunctionName"] = "fibonacci-kafka-listener",
            ["SlimFaasKafka:Bindings:0:MinPendingMessages"] = "2",
            ["SlimFaasKafka:Bindings:0:CooldownSeconds"] = "30",
            ["SlimFaasKafka:Bindings:0:ActivityKeepAliveSeconds"] = "60",
            ["SlimFaasKafka:Bindings:0:MinConsumedDeltaForActivity"] = "5"
        };

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(inMemorySettings!)
            .Build();

        var services = new ServiceCollection();
        services.Configure<BindingsOptions>(config.GetSection("SlimFaasKafka"));
        var provider = services.BuildServiceProvider();

        // Act
        var options = provider.GetRequiredService<IOptions<BindingsOptions>>().Value;

        // Assert
        Assert.Single(options.Bindings);

        var binding = options.Bindings[0];
        Assert.Equal("fibo-public", binding.Topic);
        Assert.Equal("fibonacci-group", binding.ConsumerGroupId);
        Assert.Equal("fibonacci-kafka-listener", binding.FunctionName);
        Assert.Equal(2, binding.MinPendingMessages);
        Assert.Equal(30, binding.CooldownSeconds);
        Assert.Equal(60, binding.ActivityKeepAliveSeconds);
        Assert.Equal(5, binding.MinConsumedDeltaForActivity);
    }
}
